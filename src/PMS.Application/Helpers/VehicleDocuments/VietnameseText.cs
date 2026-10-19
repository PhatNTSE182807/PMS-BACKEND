using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PMS.Application.Helpers.VehicleDocuments;

/// <summary>
/// Text helpers for comparing Vietnamese words read by OCR with what the user typed:
/// diacritics are removed ("NGUYỄN TẤN PHÁT" → "NGUYEN TAN PHAT"), case and spacing ignored.
/// </summary>
public static class VietnameseText
{
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Removes diacritics and turns đ/Đ into d/D, keeping exactly one output character per input character
    /// (after NFC), so a position found in the folded text is the same position in the original.
    /// </summary>
    public static string Fold(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var composed = text.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(composed.Length);
        foreach (var c in composed)
        {
            if (c is 'đ') { builder.Append('d'); continue; }
            if (c is 'Đ') { builder.Append('D'); continue; }

            // The first non-mark character of the canonical decomposition is the base letter
            var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
            var baseChar = decomposed.FirstOrDefault(d => CharUnicodeInfo.GetUnicodeCategory(d) != UnicodeCategory.NonSpacingMark);
            builder.Append(baseChar == default ? c : baseChar);
        }

        return builder.ToString();
    }

    /// <summary>Folded, upper case, letters and digits only, single spaces: "  Nguyễn  tấn-Phát " → "NGUYEN TAN PHAT".</summary>
    public static string NormalizeWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var folded = Fold(text).ToUpperInvariant();
        var lettersOnly = new string(folded.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
        return Spaces.Replace(lettersOnly, " ").Trim();
    }

    /// <summary>Folded, upper case, letters and digits only, no spaces: "Mercedes-Benz" → "MERCEDESBENZ".</summary>
    public static string Compact(string text) => NormalizeWords(text).Replace(" ", string.Empty);

    /// <summary>1 when equal, 0 when nothing in common (1 - Levenshtein distance / longer length).</summary>
    public static double Similarity(string a, string b)
    {
        a ??= string.Empty;
        b ??= string.Empty;
        if (a.Length == 0 && b.Length == 0)
            return 1;

        var distance = Levenshtein(a, b);
        return 1.0 - (double)distance / Math.Max(a.Length, b.Length);
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
