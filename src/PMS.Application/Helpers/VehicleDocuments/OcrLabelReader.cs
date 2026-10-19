using System;
using System.Collections.Generic;
using System.Linq;
using PMS.Application.Services.Ocr;

namespace PMS.Application.Helpers.VehicleDocuments;

/// <summary>A value read from a document and the OCR confidence of the line it came from (0..1).</summary>
public record ParsedValue(string Value, double Confidence);

/// <summary>
/// Finds "label: value" pairs in OCR lines. Vietnamese papers print bilingual labels such as
/// "Tên chủ xe (Owner's full name): NGUYỄN VĂN A" or put the value on the next line, and sometimes
/// two fields share a line ("Nhãn hiệu: TOYOTA  Số loại: CAMRY"), so the value stops at the next label.
/// Labels are matched on folded lower-case text (no diacritics).
/// </summary>
public static class OcrLabelReader
{
    private static readonly char[] TrimChars = [' ', ':', '/', '-', '.', ',', ';', '|', '(', ')', '\t'];

    /// <summary>
    /// The value after the first line holding one of <paramref name="labels"/>. Text after any of
    /// <paramref name="stopLabels"/> on the same line is cut off. When the label line has no value,
    /// the next line is used if it is not itself a label. <paramref name="accept"/> filters candidates.
    /// </summary>
    public static ParsedValue Read(
        IReadOnlyList<OcrLine> lines,
        IReadOnlyCollection<string> labels,
        IReadOnlyCollection<string> stopLabels,
        Func<string, bool> accept = null)
    {
        accept ??= v => v.Any(char.IsLetterOrDigit);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Text ?? string.Empty;
            var folded = VietnameseText.Fold(line).ToLowerInvariant();
            var labelEnd = LastLabelEnd(folded, labels);
            if (labelEnd < 0)
                continue;

            var value = CleanValue(line, folded, labelEnd, stopLabels.Concat(labels).ToList());
            if (value.Length > 0 && accept(value))
                return new ParsedValue(value, lines[i].Confidence);

            // Label alone on its line: the value is printed underneath
            if (i + 1 < lines.Count)
            {
                var next = lines[i + 1].Text ?? string.Empty;
                var nextFolded = VietnameseText.Fold(next).ToLowerInvariant();
                if (FirstLabelStart(nextFolded, stopLabels.Concat(labels).ToList()) != 0)
                {
                    var nextValue = CleanValue(next, nextFolded, 0, stopLabels.Concat(labels).ToList());
                    if (nextValue.Length > 0 && accept(nextValue))
                        return new ParsedValue(nextValue, lines[i + 1].Confidence);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// End index of the label in the line, or -1. The left-most label wins, then labels that follow it
    /// closely extend it (bilingual labels: "Họ và tên / Full name", "Tên chủ xe (Owner's full name)").
    /// </summary>
    private static int LastLabelEnd(string folded, IReadOnlyCollection<string> labels)
    {
        var start = FirstLabelStart(folded, labels);
        if (start < 0)
            return -1;

        var end = labels
            .Where(l => IndexOfWord(folded, l, start) == start)
            .Max(l => start + l.Length);

        bool extended;
        do
        {
            extended = false;
            foreach (var label in labels)
            {
                var index = IndexOfWord(folded, label, end);
                if (index >= end && index - end <= 5 && index + label.Length > end)
                {
                    end = index + label.Length;
                    extended = true;
                }
            }
        } while (extended);

        // Skip a following "(English label)" group and its colon
        if (end >= 0)
        {
            var rest = folded[end..];
            var colon = rest.IndexOf(':');
            var paren = rest.TrimStart().StartsWith('(') ? rest.IndexOf(')') : -1;
            if (paren >= 0) end += paren + 1;
            else if (colon >= 0 && colon <= 30 && !rest[..colon].Any(char.IsDigit)) end += colon + 1;
        }

        return end;
    }

    private static int FirstLabelStart(string folded, IEnumerable<string> labels)
    {
        var start = -1;
        foreach (var label in labels)
        {
            var index = IndexOfWord(folded, label, 0);
            if (index >= 0 && (start < 0 || index < start))
                start = index;
        }

        return start;
    }

    /// <summary>The original-text value between <paramref name="from"/> and the next label, trimmed.</summary>
    private static string CleanValue(string original, string folded, int from, IReadOnlyCollection<string> stopLabels)
    {
        var composed = original.Normalize(System.Text.NormalizationForm.FormC);
        var to = composed.Length;
        foreach (var stop in stopLabels)
        {
            var index = IndexOfWord(folded, stop, from + 1);
            if (index > from && index < to)
                to = index;
        }

        if (from >= to)
            return string.Empty;

        var value = composed[from..to].Trim(TrimChars);
        // Drop a leftover "(English label):" prefix
        if (value.StartsWith('(') && value.Contains(')'))
            value = value[(value.IndexOf(')') + 1)..].Trim(TrimChars);
        return value;
    }

    /// <summary>Index of <paramref name="word"/> not glued to letters on either side, or -1.</summary>
    private static int IndexOfWord(string text, string word, int startIndex)
    {
        while (startIndex <= text.Length)
        {
            var index = text.IndexOf(word, startIndex, StringComparison.Ordinal);
            if (index < 0)
                return -1;

            var before = index == 0 || !char.IsLetter(text[index - 1]);
            var afterIndex = index + word.Length;
            var after = afterIndex >= text.Length || !char.IsLetter(text[afterIndex]);
            if (before && after)
                return index;

            startIndex = index + 1;
        }

        return -1;
    }
}
