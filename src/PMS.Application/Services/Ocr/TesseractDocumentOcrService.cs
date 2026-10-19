using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Extensions.Logging;
using Tesseract;

namespace PMS.Application.Services.Ocr;

/// <summary>
/// On-server OCR with Tesseract 5 (NuGet "Tesseract", native Windows x64 binaries included; no Python).
/// Needs vie.traineddata and eng.traineddata in <see cref="OcrOptions.TessDataPath"/> (run src/PMS.API/tessdata/get-tessdata.ps1 once).
/// PDFs are rendered page by page with PDFium (Docnet.Core) first. Failures return
/// <see cref="OcrResult.Failed"/> so the check goes to Staff instead of crashing the job.
/// </summary>
public class TesseractDocumentOcrService(OcrOptions options, string contentRootPath, ILogger<TesseractDocumentOcrService> logger)
    : IDocumentOcrService
{
    /// <summary>At most this many PDF pages are read (a paper has a front and a back).</summary>
    private const int MaxPdfPages = 2;

    /// <summary>PDF pages are rendered about 200 dpi on A4, enough for Tesseract.</summary>
    private const int PdfRenderWidth = 1654;
    private const int PdfRenderHeight = 2339;

    public string EngineName => "Tesseract";

    public async Task<OcrResult> ReadAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();

        var dataPath = Path.IsPathRooted(options.TessDataPath)
            ? options.TessDataPath
            : Path.Combine(contentRootPath, options.TessDataPath);

        foreach (var language in options.Languages.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!File.Exists(Path.Combine(dataPath, $"{language}.traineddata")))
                return OcrResult.Failed($"Tesseract language data '{language}.traineddata' is missing in {dataPath}.");
        }

        try
        {
            // The engine is not thread-safe: one per file, on a worker thread (the job runs in the background anyway)
            return await Task.Run(() =>
            {
                using var engine = new TesseractEngine(dataPath, options.Languages, EngineMode.LstmOnly);
                var lines = new List<OcrLine>();
                foreach (var image in ToImages(bytes, contentType))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var pix = Pix.LoadFromMemory(image);
                    using var page = engine.Process(pix, PageSegMode.Auto);
                    lines.AddRange(ReadLines(page));
                }

                return OcrResult.FromLines(lines);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Tesseract OCR failed");
            return OcrResult.Failed($"OCR failed: {ex.Message}");
        }
    }

    /// <summary>Text lines with Tesseract's 0-100 confidence scaled to 0..1.</summary>
    private static IEnumerable<OcrLine> ReadLines(Page page)
    {
        var lines = new List<OcrLine>();
        using var iterator = page.GetIterator();
        iterator.Begin();
        do
        {
            var text = iterator.GetText(PageIteratorLevel.TextLine)?.Trim();
            if (!string.IsNullOrEmpty(text))
                lines.Add(new OcrLine(text, iterator.GetConfidence(PageIteratorLevel.TextLine) / 100.0));
        } while (iterator.Next(PageIteratorLevel.TextLine));

        return lines;
    }

    /// <summary>JPEG/PNG as they are; each PDF page rendered to a white-background BMP.</summary>
    private static IEnumerable<byte[]> ToImages(byte[] bytes, string contentType)
    {
        if (contentType != "application/pdf")
        {
            yield return bytes;
            yield break;
        }

        using var document = DocLib.Instance.GetDocReader(bytes, new PageDimensions(PdfRenderWidth, PdfRenderHeight));
        var pages = Math.Min(document.GetPageCount(), MaxPdfPages);
        for (var i = 0; i < pages; i++)
        {
            using var page = document.GetPageReader(i);
            yield return BgraToBmp(page.GetImage(), page.GetPageWidth(), page.GetPageHeight());
        }
    }

    /// <summary>
    /// PDFium renders BGRA with a transparent background; this flattens it onto white and writes a
    /// 24-bit bottom-up BMP, which Leptonica reads without extra codecs.
    /// </summary>
    internal static byte[] BgraToBmp(byte[] bgra, int width, int height)
    {
        var rowSize = (width * 3 + 3) & ~3;
        var pixelBytes = rowSize * height;
        var bmp = new byte[54 + pixelBytes];

        // BITMAPFILEHEADER + BITMAPINFOHEADER
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(width).CopyTo(bmp, 18);
        BitConverter.GetBytes(height).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
        BitConverter.GetBytes(pixelBytes).CopyTo(bmp, 34);

        for (var y = 0; y < height; y++)
        {
            var target = 54 + (height - 1 - y) * rowSize;
            for (var x = 0; x < width; x++)
            {
                var source = (y * width + x) * 4;
                var alpha = bgra[source + 3];
                for (var channel = 0; channel < 3; channel++)
                    bmp[target + x * 3 + channel] = (byte)((bgra[source + channel] * alpha + 255 * (255 - alpha)) / 255);
            }
        }

        return bmp;
    }
}
