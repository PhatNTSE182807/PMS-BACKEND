using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using PMS.Application.Services.Ocr;

namespace PMS.Application.Helpers.VehicleDocuments;

/// <summary>What OCR found on the vehicle registration certificate (Giấy chứng nhận đăng ký xe, "cà vẹt").</summary>
public class RegistrationCertificateData
{
    public ParsedValue Plate { get; init; }
    public ParsedValue Brand { get; init; }
    public ParsedValue Model { get; init; }
    public ParsedValue Color { get; init; }
    public ParsedValue OwnerName { get; init; }
}

/// <summary>What OCR found on the Citizen ID card (Căn cước công dân / Căn cước, "CCCD").</summary>
public class CitizenIdCardData
{
    /// <summary>The 12-digit personal identification number.</summary>
    public ParsedValue Number { get; init; }
    public ParsedValue FullName { get; init; }
    /// <summary>Date of birth as yyyy-MM-dd.</summary>
    public ParsedValue DateOfBirth { get; init; }
}

/// <summary>Reads the cà vẹt fields from OCR lines (front and back pages concatenated).</summary>
public static class RegistrationCertificateParser
{
    private static readonly string[] OwnerLabels = ["ten chu xe", "owner's full name", "owner s full name", "owners full name", "chu xe", "owner"];
    private static readonly string[] BrandLabels = ["nhan hieu", "brand"];
    private static readonly string[] ModelLabels = ["so loai", "model code", "model"];
    private static readonly string[] ColorLabels = ["mau son", "colour", "color"];
    private static readonly string[] PlateLabels = ["bien so dang ky", "bien kiem soat", "bien so", "number plate"];

    /// <summary>Other labels printed on the card: a value never runs into them.</summary>
    private static readonly string[] OtherLabels =
    [
        "dia chi", "address", "so may", "engine", "so khung", "chassis", "loai xe", "dung tich", "capacity",
        "so cho", "seat", "dang ky lan dau", "ngay dang ky", "date of issue", "co gia tri den", "valid until"
    ];

    private static readonly string[] AllLabels = OwnerLabels.Concat(BrandLabels).Concat(ModelLabels)
        .Concat(ColorLabels).Concat(PlateLabels).Concat(OtherLabels).ToArray();

    public static RegistrationCertificateData Parse(IReadOnlyList<OcrLine> lines)
    {
        lines ??= [];
        var hasLetters = (Func<string, bool>)(v => v.Count(char.IsLetter) >= 2);

        return new RegistrationCertificateData
        {
            OwnerName = OcrLabelReader.Read(lines, OwnerLabels, AllLabels, v => hasLetters(v) && !v.Any(char.IsDigit)),
            Brand = OcrLabelReader.Read(lines, BrandLabels, AllLabels, hasLetters),
            Model = OcrLabelReader.Read(lines, ModelLabels, AllLabels),
            Color = OcrLabelReader.Read(lines, ColorLabels, AllLabels, hasLetters),
            Plate = ReadPlate(lines)
        };
    }

    /// <summary>The plate next to its label, else the first plate-shaped text anywhere on the card.</summary>
    private static ParsedValue ReadPlate(IReadOnlyList<OcrLine> lines)
    {
        var labelled = OcrLabelReader.Read(lines, PlateLabels, AllLabels, v => PlateText.FindPlates(v).Any());
        if (labelled != null)
            return new ParsedValue(PlateText.Format(PlateText.FindPlates(labelled.Value).First()), labelled.Confidence);

        foreach (var line in lines)
        {
            var plate = PlateText.FindPlates(line.Text).FirstOrDefault();
            if (plate != null)
                return new ParsedValue(PlateText.Format(plate), line.Confidence);
        }

        return null;
    }
}

/// <summary>Reads the CCCD fields from OCR lines (front and back pages concatenated).</summary>
public static class CitizenIdCardParser
{
    private static readonly string[] NameLabels = ["ho, chu dem va ten", "ho chu dem va ten", "ho va ten", "full name"];
    private static readonly string[] BirthLabels = ["ngay, thang, nam sinh", "ngay thang nam sinh", "ngay sinh", "date of birth"];

    private static readonly string[] OtherLabels =
    [
        "so dinh danh ca nhan", "personal identification number", "gioi tinh", "sex", "quoc tich", "nationality",
        "que quan", "place of origin", "noi thuong tru", "place of residence", "noi cu tru", "place of birth",
        "noi dang ky khai sinh", "co gia tri den", "date of expiry", "can cuoc cong dan", "citizen identity card"
    ];

    private static readonly string[] AllLabels = NameLabels.Concat(BirthLabels).Concat(OtherLabels).ToArray();

    /// <summary>MRZ line 1 on the back: "IDVNM" + 9-digit card number + check digit + the 12-digit ID.</summary>
    private static readonly Regex MrzNumber = new(@"IDVNM\d{9}[\d<]?(\d{12})", RegexOptions.Compiled);

    /// <summary>MRZ name line: "NGUYEN<<TAN<PHAT<<<<".</summary>
    private static readonly Regex MrzName = new(@"^([A-Z]+)<<([A-Z<]+?)<*$", RegexOptions.Compiled);

    /// <summary>12 digits, possibly printed in groups ("0792 0400 6789").</summary>
    private static readonly Regex TwelveDigits = new(@"(?<!\d)(?:\d[ ]?){11}\d(?!\d)", RegexOptions.Compiled);

    private static readonly Regex Date = new(@"(?<!\d)(\d{1,2})\s*[/\-.]\s*(\d{1,2})\s*[/\-.]\s*(\d{4})(?!\d)", RegexOptions.Compiled);

    public static CitizenIdCardData Parse(IReadOnlyList<OcrLine> lines)
    {
        lines ??= [];
        return new CitizenIdCardData
        {
            Number = ReadNumber(lines),
            FullName = OcrLabelReader.Read(lines, NameLabels, AllLabels, v => v.Count(char.IsLetter) >= 2 && !v.Any(char.IsDigit))
                       ?? ReadMrzName(lines),
            DateOfBirth = ReadBirthDate(lines)
        };
    }

    /// <summary>The ID from the MRZ when present (most reliable), else the first 12-digit number on the card.</summary>
    private static ParsedValue ReadNumber(IReadOnlyList<OcrLine> lines)
    {
        foreach (var line in lines)
        {
            var compact = FixDigits(line.Text).Replace(" ", string.Empty).ToUpperInvariant();
            var mrz = MrzNumber.Match(compact);
            if (mrz.Success)
                return new ParsedValue(mrz.Groups[1].Value, line.Confidence);
        }

        foreach (var line in lines)
        {
            var match = TwelveDigits.Match(FixDigits(line.Text));
            if (match.Success)
                return new ParsedValue(match.Value.Replace(" ", string.Empty), line.Confidence);
        }

        return null;
    }

    private static ParsedValue ReadMrzName(IReadOnlyList<OcrLine> lines)
    {
        foreach (var line in lines)
        {
            var compact = (line.Text ?? string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
            var match = MrzName.Match(compact);
            if (!match.Success)
                continue;

            var given = match.Groups[2].Value.Replace('<', ' ').Trim();
            // MRZ drops diacritics, so the name is a little less certain than the printed one
            return new ParsedValue($"{match.Groups[1].Value} {given}", line.Confidence * 0.95);
        }

        return null;
    }

    private static ParsedValue ReadBirthDate(IReadOnlyList<OcrLine> lines)
    {
        var labelled = OcrLabelReader.Read(lines, BirthLabels, AllLabels, v => Date.IsMatch(FixDigits(v)));
        if (labelled == null)
            return null;

        var match = Date.Match(FixDigits(labelled.Value));
        var day = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var year = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

        return DateOnly.TryParseExact($"{year:D4}-{month:D2}-{day:D2}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? new ParsedValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), labelled.Confidence)
            : null;
    }

    /// <summary>OCR look-alikes inside numbers: O→0, I/l/|→1.</summary>
    private static string FixDigits(string text) =>
        Regex.Replace(text ?? string.Empty, @"(?<=\d)[OoIl|]|[OoIl|](?=\d)", m => m.Value is "O" or "o" ? "0" : "1");
}
