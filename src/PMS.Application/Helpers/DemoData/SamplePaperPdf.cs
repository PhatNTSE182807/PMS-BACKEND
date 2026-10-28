using System.Globalization;
using System.Text;

namespace PMS.Application.Helpers.DemoData;

/// <summary>
/// Builds a one-page PDF that stands in for a photo of a vehicle paper in demo data: a card with a
/// title, a few "label: value" lines and a "SAMPLE" mark, so Staff can compare it with the account.
/// Plain PDF 1.4 with the built-in Helvetica fonts; text is reduced to ASCII (no Vietnamese marks).
/// </summary>
public static class SamplePaperPdf
{
    private const int Width = 600;

    private const int Height = 380;

    public static byte[] Create(string title, IReadOnlyList<(string Label, string Value)> lines)
    {
        var content = new StringBuilder();
        // Card background and border
        content.Append("0.95 0.97 1 rg 16 16 568 348 re f\n");
        content.Append("0.20 0.35 0.60 RG 2 w 16 16 568 348 re S\n");
        // Header band
        content.Append("0.20 0.35 0.60 rg 16 312 568 52 re f\n");
        content.Append("BT /F2 20 Tf 1 1 1 rg 32 330 Td (").Append(Escape(title)).Append(") Tj ET\n");

        var y = 276;
        foreach (var (label, value) in lines)
        {
            content.Append("BT /F1 12 Tf 0.35 0.35 0.40 rg 32 ").Append(y).Append(" Td (").Append(Escape(label)).Append(") Tj ET\n");
            content.Append("BT /F2 14 Tf 0.10 0.10 0.15 rg 200 ").Append(y).Append(" Td (").Append(Escape(value)).Append(") Tj ET\n");
            y -= 30;
        }

        content.Append("BT /F2 11 Tf 0.80 0.15 0.15 rg 32 30 Td (SAMPLE - DEMO DATA, NOT A REAL DOCUMENT) Tj ET\n");

        var stream = content.ToString();
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Width} {Height}] /Resources << /Font << /F1 4 0 R /F2 5 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream"
        };

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 ").Append(objects.Length + 1).Append('\n');
        pdf.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        pdf.Append("startxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>"Nguyễn Văn Đạt" → "Nguyen Van Dat": Helvetica has no Vietnamese letters.</summary>
    public static string ToAscii(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var decomposed = text.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(c is >= ' ' and <= '~' ? c : '?');
        }

        return builder.ToString();
    }

    private static string Escape(string text) =>
        ToAscii(text).Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
