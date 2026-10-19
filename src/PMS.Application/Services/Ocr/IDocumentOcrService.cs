using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PMS.Application.Services.Ocr;

/// <summary>
/// Reads the text of a photo or PDF of a vehicle paper. The engine is swappable through configuration
/// ("Ocr:Engine"): Tesseract on the server today, a cloud OCR later, a deterministic fake in tests.
/// </summary>
public interface IDocumentOcrService
{
    /// <summary>Short engine name stored with the result, e.g. "Tesseract" or "Fake".</summary>
    string EngineName { get; }

    /// <param name="content">The file bytes; the stream is read to the end but not disposed.</param>
    /// <param name="contentType">image/jpeg, image/png or application/pdf.</param>
    Task<OcrResult> ReadAsync(Stream content, string contentType, CancellationToken cancellationToken = default);
}

/// <summary>One line of recognised text and how sure the engine is about it (0..1).</summary>
public record OcrLine(string Text, double Confidence);

/// <summary>The lines read from one file, top to bottom. <see cref="Error"/> is set when the engine could not run.</summary>
public class OcrResult
{
    public IReadOnlyList<OcrLine> Lines { get; init; } = [];

    public string Error { get; init; }

    public bool Succeeded => Error == null;

    public static OcrResult FromLines(IReadOnlyList<OcrLine> lines) => new() { Lines = lines };

    public static OcrResult Failed(string error) => new() { Error = error };
}
