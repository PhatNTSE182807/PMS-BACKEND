using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PMS.Application.Services.Ocr;

/// <summary>
/// Deterministic OCR for tests and demos ("Ocr:Engine": "Fake"). It "reads" the text embedded after an
/// <c>OCR-TEXT:</c> marker anywhere in the file (e.g. appended to a PNG), one line per text line.
/// A line may start with "@0.30 " to set its confidence (default 0.95). No marker means nothing readable.
/// </summary>
public class FakeDocumentOcrService : IDocumentOcrService
{
    public const string Marker = "OCR-TEXT:";

    private const double DefaultConfidence = 0.95;

    public string EngineName => "Fake";

    public async Task<OcrResult> ReadAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);

        // Latin-1 keeps one char per byte, so the marker index is also the byte index
        var bytes = buffer.ToArray();
        var index = Encoding.Latin1.GetString(bytes).IndexOf(Marker, StringComparison.Ordinal);
        if (index < 0)
            return OcrResult.FromLines([]);

        var text = Encoding.UTF8.GetString(bytes, index + Marker.Length, bytes.Length - index - Marker.Length);
        var lines = text.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length > 0)
            .Select(ToLine)
            .ToList();

        return OcrResult.FromLines(lines);
    }

    private static OcrLine ToLine(string line)
    {
        if (line.StartsWith('@'))
        {
            var space = line.IndexOf(' ');
            if (space > 1 && double.TryParse(line[1..space], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence))
                return new OcrLine(line[(space + 1)..].Trim(), confidence);
        }

        return new OcrLine(line.Trim(), DefaultConfidence);
    }
}
