namespace PMS.Application.Services.Ocr;

/// <summary>"Ocr" configuration section: which engine reads the papers and how sure it must be.</summary>
public class OcrOptions
{
    public const string Section = "Ocr";

    /// <summary>"Tesseract" (default) or "Fake" (tests and demos: reads text embedded in the file).</summary>
    public string Engine { get; set; } = "Tesseract";

    /// <summary>Folder with vie.traineddata and eng.traineddata, absolute or relative to the API's content root.</summary>
    public string TessDataPath { get; set; } = "tessdata";

    /// <summary>Tesseract languages, Vietnamese first.</summary>
    public string Languages { get; set; } = "vie+eng";

    /// <summary>Every field must reach this confidence (0..1) to be verified without Staff.</summary>
    public double AutoVerifyConfidence { get; set; } = 0.85;

    /// <summary>Below this OCR confidence (0..1) a value counts as unreadable.</summary>
    public double UnreadableConfidence { get; set; } = 0.40;
}

/// <summary>"VehicleDocuments" configuration section: where the photos are kept and for how long.</summary>
public class VehicleDocumentStorageOptions
{
    public const string Section = "VehicleDocuments";

    /// <summary>Private folder (never wwwroot), absolute or relative to the API's content root.</summary>
    public string StorageRoot { get; set; } = "App_Data/vehicle-documents";

    /// <summary>Largest accepted file: 10 MB.</summary>
    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>BR-26: photos are deleted this many days after the final decision.</summary>
    public int RetentionDays { get; set; } = 30;
}
