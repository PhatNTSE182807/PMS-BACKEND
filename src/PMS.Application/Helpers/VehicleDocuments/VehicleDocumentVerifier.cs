using System;
using System.Collections.Generic;
using System.Linq;

namespace PMS.Application.Helpers.VehicleDocuments;

/// <summary>Outcome of one field: what was typed or stored, what OCR read, and whether they agree.</summary>
public class FieldCheck
{
    /// <summary>RegistrationCertificate or CitizenId.</summary>
    public string Document { get; set; }

    /// <summary>plate, brand, model, color, ownerName, citizenId, fullName or dateOfBirth.</summary>
    public string Field { get; set; }

    public string Label { get; set; }

    public string Expected { get; set; }

    public string Read { get; set; }

    /// <summary>Match, Mismatch or Unreadable.</summary>
    public string Result { get; set; }

    /// <summary>0..1: OCR confidence of the line times how close the values are.</summary>
    public double Confidence { get; set; }

    public string Note { get; set; }
}

/// <summary>The data the papers are checked against: the vehicle form and the user's profile.</summary>
public class VerificationExpectations
{
    public string Plate { get; init; }
    public string Brand { get; init; }
    public string Model { get; init; }
    public string Color { get; init; }

    /// <summary>The account holder's name; the cà vẹt owner should be them when the vehicle is in their name.</summary>
    public string ProfileFullName { get; init; }

    /// <summary>Citizen ID stored in the user's profile; the uploaded CCCD must carry it.</summary>
    public string ProfileCitizenId { get; init; }

    public DateOnly? ProfileDateOfBirth { get; init; }

    /// <summary>True when the vehicle's owner Citizen ID is not the user's own (vehicle in another person's name).</summary>
    public bool OwnedBySomeoneElse { get; init; }
}

public class VerificationThresholds
{
    /// <summary>Below this OCR confidence a value is treated as unreadable.</summary>
    public double Unreadable { get; init; } = 0.40;

    /// <summary>Every field must reach this confidence to be verified without a person (never on low confidence).</summary>
    public double AutoVerify { get; init; } = 0.85;
}

public class VerificationOutcome
{
    public List<FieldCheck> Fields { get; init; } = [];

    /// <summary>AutoVerified, NeedsReview or Rejected.</summary>
    public string Status { get; init; }

    public string Reason { get; init; }

    /// <summary>Lowest field confidence (0 when nothing was read).</summary>
    public double Confidence { get; init; }
}

/// <summary>
/// Compares what OCR read on the cà vẹt and the CCCD with the vehicle form and the profile
/// (SRS FR-VER-02/03). Only an all-match, high-confidence result is auto-verified; any mismatch,
/// unreadable or low-confidence field goes to Staff review; papers that cannot be read at all are rejected.
/// </summary>
public static class VehicleDocumentVerifier
{
    public const string Match = "Match";
    public const string Mismatch = "Mismatch";
    public const string Unreadable = "Unreadable";

    public const string RegistrationCertificate = "RegistrationCertificate";
    public const string CitizenId = "CitizenId";

    public static VerificationOutcome Verify(
        RegistrationCertificateData certificate,
        CitizenIdCardData card,
        VerificationExpectations expected,
        VerificationThresholds thresholds = null)
    {
        thresholds ??= new VerificationThresholds();
        certificate ??= new RegistrationCertificateData();
        card ??= new CitizenIdCardData();

        var fields = new List<FieldCheck>
        {
            Check(RegistrationCertificate, "plate", "Plate number", expected.Plate, certificate.Plate, ComparePlate, thresholds),
            Check(RegistrationCertificate, "brand", "Brand", expected.Brand, certificate.Brand, CompareBrand, thresholds),
            Check(RegistrationCertificate, "model", "Model", expected.Model, certificate.Model, CompareModel, thresholds),
            Check(RegistrationCertificate, "color", "Colour", expected.Color, certificate.Color, CompareColor, thresholds),
            CheckOwner(expected, certificate.OwnerName, thresholds),
            Check(CitizenId, "citizenId", "Citizen ID number", expected.ProfileCitizenId, card.Number, CompareCitizenId, thresholds,
                missingExpectedNote: "No Citizen ID in your profile to compare with."),
            Check(CitizenId, "fullName", "Full name", expected.ProfileFullName, card.FullName, CompareName, thresholds)
        };

        // Date of birth is only compared when the profile has one
        if (expected.ProfileDateOfBirth != null)
            fields.Add(Check(CitizenId, "dateOfBirth", "Date of birth", expected.ProfileDateOfBirth.Value.ToString("yyyy-MM-dd"),
                card.DateOfBirth, (e, r) => e == r ? 1 : 0, thresholds));

        var confidence = fields.Min(f => f.Confidence);
        var plateRead = fields.Single(f => f.Field == "plate").Result != Unreadable;
        var numberRead = fields.Single(f => f.Field == "citizenId").Result != Unreadable;

        if (!plateRead && !numberRead)
            return new VerificationOutcome
            {
                Fields = fields,
                Status = "Rejected",
                Confidence = confidence,
                Reason = "The photos could not be read. Upload sharp, well-lit photos of the whole registration certificate and Citizen ID card."
            };

        var problems = fields.Where(f => f.Result != Match || f.Confidence < thresholds.AutoVerify).ToList();
        if (problems.Count == 0)
            return new VerificationOutcome { Fields = fields, Status = "AutoVerified", Confidence = confidence };

        var mismatched = problems.Where(f => f.Result == Mismatch).Select(f => f.Label).ToList();
        var unreadable = problems.Where(f => f.Result == Unreadable).Select(f => f.Label).ToList();
        var unsure = problems.Where(f => f.Result == Match).Select(f => f.Label).ToList();
        var parts = new List<string>();
        if (mismatched.Count > 0) parts.Add($"does not match: {string.Join(", ", mismatched)}");
        if (unreadable.Count > 0) parts.Add($"could not be read: {string.Join(", ", unreadable)}");
        if (unsure.Count > 0) parts.Add($"read with low confidence: {string.Join(", ", unsure)}");

        return new VerificationOutcome
        {
            Fields = fields,
            Status = "NeedsReview",
            Confidence = confidence,
            Reason = $"Sent to Staff review; {string.Join("; ", parts)}."
        };
    }

    private static FieldCheck CheckOwner(VerificationExpectations expected, ParsedValue read, VerificationThresholds thresholds)
    {
        var check = Check(RegistrationCertificate, "ownerName", "Owner name", expected.ProfileFullName, read, CompareName, thresholds);
        if (expected.OwnedBySomeoneElse && check.Result != Unreadable)
        {
            // Another person's vehicle: a person checks the owner and the authorisation
            check.Result = Mismatch;
            check.Expected = null;
            check.Note = "The vehicle is registered to another person's Citizen ID; Staff will check the owner.";
        }

        return check;
    }

    private static FieldCheck Check(
        string document, string field, string label, string expected, ParsedValue read,
        Func<string, string, double> compare, VerificationThresholds thresholds, string missingExpectedNote = null)
    {
        var check = new FieldCheck { Document = document, Field = field, Label = label, Expected = expected, Read = read?.Value };

        if (read == null || string.IsNullOrWhiteSpace(read.Value) || read.Confidence < thresholds.Unreadable)
        {
            check.Result = Unreadable;
            check.Confidence = 0;
            check.Note = read == null ? "Not found on the document." : "Too blurry to read reliably.";
            return check;
        }

        if (string.IsNullOrWhiteSpace(expected))
        {
            check.Result = Mismatch;
            check.Confidence = Math.Round(read.Confidence, 4);
            check.Note = missingExpectedNote ?? "Nothing was entered to compare with.";
            return check;
        }

        var score = compare(expected, read.Value);
        check.Result = score > 0 ? Match : Mismatch;
        // A match is as sure as the OCR line and the closeness of the values; a mismatch as sure as the OCR line
        check.Confidence = Math.Round(score > 0 ? read.Confidence * score : read.Confidence, 4);
        return check;
    }

    /// <summary>1 for the same plate, 0.9 when equal after fixing OCR look-alikes, else 0.</summary>
    public static double ComparePlate(string expected, string read)
    {
        var a = PlateText.Normalize(expected);
        var b = PlateText.Normalize(read);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        return PlateText.FixOcrConfusions(a) == PlateText.FixOcrConfusions(b) ? 0.9 : 0;
    }

    /// <summary>Exact 1; one name inside the other ("Mercedes" / "MERCEDES-BENZ") 0.95; a one-letter OCR slip 0.85.</summary>
    public static double CompareBrand(string expected, string read)
    {
        var a = VietnameseText.Compact(expected);
        var b = VietnameseText.Compact(read);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        if (Math.Min(a.Length, b.Length) >= 3 && (a.Contains(b) || b.Contains(a))) return 0.95;
        return VietnameseText.Similarity(a, b) >= 0.85 ? 0.85 : 0;
    }

    /// <summary>Exact 1; one inside the other ("Camry" / "CAMRY 2.5Q") 0.9; a shared word or a close spelling 0.8.</summary>
    public static double CompareModel(string expected, string read)
    {
        var a = VietnameseText.Compact(expected);
        var b = VietnameseText.Compact(read);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        if (Math.Min(a.Length, b.Length) >= 2 && (a.Contains(b) || b.Contains(a))) return 0.9;

        var wordsA = VietnameseText.NormalizeWords(expected).Split(' ').Where(w => w.Length >= 2 && w.Any(char.IsLetter));
        var wordsB = VietnameseText.NormalizeWords(read).Split(' ').Where(w => w.Length >= 2 && w.Any(char.IsLetter));
        if (wordsA.Intersect(wordsB).Any()) return 0.8;

        return VietnameseText.Similarity(a, b) >= 0.8 ? 0.8 : 0;
    }

    /// <summary>Same colour family in Vietnamese or English ("Trắng" / "Pearl White") 1, else the words compared.</summary>
    public static double CompareColor(string expected, string read)
    {
        var a = ColorFamilies(expected);
        var b = ColorFamilies(read);
        if (a.Count > 0 && b.Count > 0)
            return a.Overlaps(b) ? 1 : 0;

        var ca = VietnameseText.Compact(expected);
        var cb = VietnameseText.Compact(read);
        if (ca.Length == 0 || cb.Length == 0) return 0;
        return ca == cb ? 1 : VietnameseText.Similarity(ca, cb) >= 0.85 ? 0.85 : 0;
    }

    /// <summary>Same name without diacritics 1; a small OCR slip (≥ 90% similar) scores its similarity.</summary>
    public static double CompareName(string expected, string read)
    {
        var a = VietnameseText.NormalizeWords(expected);
        var b = VietnameseText.NormalizeWords(read);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        var similarity = VietnameseText.Similarity(a, b);
        return similarity >= 0.9 ? similarity : 0;
    }

    /// <summary>The 12 digits must be identical: one wrong digit is another person.</summary>
    public static double CompareCitizenId(string expected, string read)
    {
        var a = new string((expected ?? string.Empty).Where(char.IsDigit).ToArray());
        var b = new string((read ?? string.Empty).Where(char.IsDigit).ToArray());
        return a.Length == 12 && a == b ? 1 : 0;
    }

    /// <summary>Folded colour words → families. "Xanh" alone is blue or green.</summary>
    private static readonly (string Word, string[] Families)[] ColorWords =
    [
        ("xanh duong", ["blue"]), ("xanh lam", ["blue"]), ("xanh la", ["green"]), ("xanh luc", ["green"]),
        ("xanh reu", ["green"]), ("xanh", ["blue", "green"]), ("trang", ["white"]), ("den", ["black"]),
        ("do", ["red"]), ("bac", ["silver", "grey"]), ("xam", ["grey", "silver"]), ("ghi", ["grey", "silver"]),
        ("vang", ["yellow", "gold"]), ("nau", ["brown"]), ("cam", ["orange"]), ("tim", ["purple"]),
        ("hong", ["pink"]), ("be", ["beige"]), ("kem", ["beige"]), ("white", ["white"]), ("black", ["black"]),
        ("red", ["red"]), ("blue", ["blue"]), ("navy", ["blue"]), ("green", ["green"]), ("silver", ["silver", "grey"]),
        ("grey", ["grey", "silver"]), ("gray", ["grey", "silver"]), ("yellow", ["yellow"]), ("gold", ["gold", "yellow"]),
        ("brown", ["brown"]), ("orange", ["orange"]), ("purple", ["purple"]), ("violet", ["purple"]),
        ("pink", ["pink"]), ("beige", ["beige"]), ("cream", ["beige"])
    ];

    private static HashSet<string> ColorFamilies(string text)
    {
        var words = $" {VietnameseText.NormalizeWords(text).ToLowerInvariant()} ";
        var families = new HashSet<string>();
        foreach (var (word, wordFamilies) in ColorWords)
        {
            var index = words.IndexOf($" {word} ", StringComparison.Ordinal);
            if (index < 0) continue;
            families.UnionWith(wordFamilies);
            // "xanh duong" must not also count as plain "xanh"
            words = words.Remove(index, word.Length + 1);
        }

        return families;
    }
}
