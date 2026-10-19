using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PMS.Application.Helpers.VehicleDocuments;

/// <summary>
/// Vietnamese plate numbers as read by OCR: "51K-992.88", "51K 992 88" and "51K99288" are the same plate.
/// OCR often swaps look-alike characters (O/0, I/1, B/8, S/5), so a second comparison fixes them by
/// position: 2 province digits, a 1-2 letter series, an optional series digit, then 4-5 digits.
/// </summary>
public static class PlateText
{
    private static readonly Regex StrictPlate = new(@"^(1[1-9]|[2-9][0-9])[A-Z]{1,2}[0-9]{4,6}$", RegexOptions.Compiled);

    /// <summary>Candidate plates inside a line: digits, a letter series, digits, with any separators.</summary>
    private static readonly Regex PlateInText = new(@"(?<![A-Z0-9])[0-9OIL]{2}[\s\-]?[A-Z0-9]{1,3}[\s\-.]?[0-9OISB]{3,4}[\s.,]?[0-9OISB]{0,2}(?![A-Z0-9])", RegexOptions.Compiled);

    private static readonly Dictionary<char, char> ToDigit = new()
    {
        ['O'] = '0', ['D'] = '0', ['Q'] = '0', ['I'] = '1', ['L'] = '1', ['Z'] = '2',
        ['S'] = '5', ['B'] = '8', ['G'] = '6', ['T'] = '7', ['A'] = '4'
    };

    private static readonly Dictionary<char, char> ToLetter = new()
    {
        ['0'] = 'D', ['1'] = 'L', ['2'] = 'Z', ['5'] = 'S', ['8'] = 'B', ['6'] = 'G', ['4'] = 'A', ['7'] = 'T'
    };

    /// <summary>Upper case, letters and digits only, Đ as D: "51k-992.88" → "51K99288".</summary>
    public static string Normalize(string plate)
    {
        if (string.IsNullOrWhiteSpace(plate))
            return string.Empty;

        var folded = VietnameseText.Fold(plate).ToUpperInvariant();
        return new string(folded.Where(char.IsAsciiLetterOrDigit).ToArray());
    }

    /// <summary>True when the normalized text has the shape of a Vietnamese plate.</summary>
    public static bool IsPlate(string normalized) => StrictPlate.IsMatch(normalized ?? string.Empty);

    /// <summary>
    /// The plate with OCR look-alikes fixed by position (e.g. "5IK99288" → "51K99288"), choosing the
    /// layout that needs the fewest changes; the input unchanged when no layout fits.
    /// </summary>
    public static string FixOcrConfusions(string normalized)
    {
        if (string.IsNullOrEmpty(normalized))
            return string.Empty;

        string best = null;
        var bestChanges = int.MaxValue;

        // Layout: 2 digits + 1-2 letters + 0-1 series digit + 4-5 digits
        foreach (var letters in new[] { 1, 2 })
        foreach (var seriesDigit in new[] { 0, 1 })
        foreach (var number in new[] { 4, 5 })
        {
            if (2 + letters + seriesDigit + number != normalized.Length)
                continue;

            var candidate = new StringBuilder(normalized.Length);
            var changes = 0;
            var valid = true;
            for (var i = 0; i < normalized.Length && valid; i++)
            {
                var wantsLetter = i >= 2 && i < 2 + letters;
                var c = normalized[i];
                if (wantsLetter == char.IsAsciiLetter(c))
                {
                    candidate.Append(c);
                    continue;
                }

                var map = wantsLetter ? ToLetter : ToDigit;
                if (map.TryGetValue(c, out var fixedChar)) { candidate.Append(fixedChar); changes++; }
                else valid = false;
            }

            if (valid && changes < bestChanges && IsPlate(candidate.ToString()))
            {
                best = candidate.ToString();
                bestChanges = changes;
            }
        }

        return best ?? normalized;
    }

    /// <summary>Display form of a normalized plate: "51K99288" → "51K-992.88", "30E1234" → "30E-1234".</summary>
    public static string Format(string normalized)
    {
        // Lazy series digit: "51K99288" is 51K + 992.88, not 51K9 + 9288
        var match = Regex.Match(normalized ?? string.Empty, @"^(\d{2}[A-Z]{1,2}\d??)(\d{4,5})$");
        if (!match.Success)
            return normalized;

        var number = match.Groups[2].Value;
        return $"{match.Groups[1].Value}-{(number.Length == 5 ? $"{number[..3]}.{number[3..]}" : number)}";
    }

    /// <summary>Plates found in a line of OCR text, normalized and with look-alikes fixed.</summary>
    public static IEnumerable<string> FindPlates(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            yield break;

        var upper = VietnameseText.Fold(line).ToUpperInvariant();
        foreach (Match match in PlateInText.Matches(upper))
        {
            var candidate = FixOcrConfusions(Normalize(match.Value));
            if (IsPlate(candidate))
                yield return candidate;
        }
    }
}
