using System;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// One uploaded photo or PDF of a vehicle's papers. The file itself lives in private storage
/// (never under wwwroot) at <see cref="StoragePath"/> and is only served to its owner and to Staff.
/// </summary>
public partial class VehicleDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid VerificationId { get; set; }

    /// <summary>RegistrationCertificate (cà vẹt) or CitizenId (CCCD).</summary>
    public string DocumentType { get; set; }

    /// <summary>Front or Back.</summary>
    public string Side { get; set; }

    /// <summary>Path relative to the private storage root; null once the file was deleted (BR-26).</summary>
    public string StoragePath { get; set; }

    public string OriginalFileName { get; set; }

    /// <summary>image/jpeg, image/png or application/pdf, detected from the file's own bytes.</summary>
    public string ContentType { get; set; }

    public long SizeBytes { get; set; }

    public DateTime UploadedAt { get; set; }

    public virtual VehicleVerification Verification { get; set; }
}
