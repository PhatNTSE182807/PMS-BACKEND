namespace PMS.Application.Services;

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
