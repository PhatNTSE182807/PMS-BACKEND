using System;
using System.Collections.Generic;
using System.Linq;

namespace PMS.Application.Models.VehicleDocuments;

/// <summary>Document kinds and sides accepted by the upload endpoint, and the files a check needs.</summary>
public static class VehicleDocumentKinds
{
    /// <summary>Vehicle registration certificate (cà vẹt).</summary>
    public const string RegistrationCertificate = "RegistrationCertificate";
    /// <summary>Citizen ID card (CCCD) of the uploader.</summary>
    public const string CitizenId = "CitizenId";
    public const string Front = "Front";
    public const string Back = "Back";

    public static readonly string[] Types = [RegistrationCertificate, CitizenId];
    public static readonly string[] Sides = [Front, Back];

    /// <summary>
    /// Every file a check needs before it can be submitted: the front of the registration certificate
    /// and both sides of the Citizen ID card. The back of the certificate is not asked for.
    /// </summary>
    public static readonly (string Type, string Side)[] Required =
    [
        (RegistrationCertificate, Front),
        (CitizenId, Front),
        (CitizenId, Back)
    ];

    public static bool IsRequired(string type, string side) => Required.Any(r => r.Type == type && r.Side == side);
}

/// <summary>
/// Draft while files are uploaded, NeedsReview once submitted (Staff compare the papers by eye),
/// then Approved or Rejected. Pending, Processing and AutoVerified only exist on checks made while
/// papers were still read by OCR; they are no longer produced.
/// </summary>
public static class VehicleVerificationStatus
{
    /// <summary>Files are being uploaded; nothing was submitted yet.</summary>
    public const string Draft = "Draft";
    /// <summary>Legacy: submitted while OCR was in use. Treated like NeedsReview.</summary>
    public const string Pending = "Pending";
    /// <summary>Legacy: OCR was running. Treated like NeedsReview.</summary>
    public const string Processing = "Processing";
    /// <summary>Legacy: verified by OCR without Staff. Counts as approved.</summary>
    public const string AutoVerified = "AutoVerified";
    /// <summary>Submitted and waiting for Staff to compare the papers (FR-VER-03).</summary>
    public const string NeedsReview = "NeedsReview";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";

    /// <summary>Statuses that still wait for a Staff decision.</summary>
    public static readonly string[] Waiting = [NeedsReview, Pending, Processing];

    /// <summary>Statuses that let the vehicle be booked and recognised at the gates.</summary>
    public static readonly string[] Verified = [Approved, AutoVerified];
}

/// <summary>One uploaded file (its bytes are fetched from the file endpoint).</summary>
public class VehicleDocumentFileModel
{
    public Guid DocumentId { get; set; }
    public string DocumentType { get; set; }
    public string Side { get; set; }
    public string FileName { get; set; }
    public string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public DateTime UploadedAt { get; set; }
    /// <summary>False once the photo was deleted (BR-26, 30 days after the decision).</summary>
    public bool IsAvailable { get; set; }
}

/// <summary>A vehicle paper check as the uploader (and Staff) see it.</summary>
public class VehicleVerificationModel
{
    public Guid VerificationId { get; set; }
    public Guid VehicleId { get; set; }
    public string LicensePlate { get; set; }
    /// <summary>Draft, NeedsReview, Approved or Rejected (legacy checks may still show Pending, Processing or AutoVerified).</summary>
    public string Status { get; set; }
    public string StatusReason { get; set; }
    public List<VehicleDocumentFileModel> Documents { get; set; } = [];
    /// <summary>"CitizenId/Back"-style entries still needed before the check can be submitted.</summary>
    public List<string> MissingDocuments { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string ReviewNote { get; set; }
    /// <summary>When the photos will be (or were) deleted under BR-26.</summary>
    public DateTime? FilesDeleteAt { get; set; }
    public DateTime? FilesDeletedAt { get; set; }
}

/// <summary>A check in the Staff review queue, with the account and vehicle data to compare the papers with.</summary>
public class VehicleVerificationReviewModel : VehicleVerificationModel
{
    public string CustomerName { get; set; }
    public string CustomerCitizenId { get; set; }
    public DateOnly? CustomerDateOfBirth { get; set; }
    public string VehicleType { get; set; }
    public string Brand { get; set; }
    public string Model { get; set; }
    public string Color { get; set; }
    /// <summary>Citizen ID of the registered owner typed on the vehicle form (may differ from the customer's).</summary>
    public string OwnerCitizenId { get; set; }
}

/// <summary>Staff decision on a check waiting for review.</summary>
public class ReviewVehicleVerificationModel
{
    public bool Approve { get; set; }

    /// <summary>Required when rejecting: the reason shown to the customer.</summary>
    public string Note { get; set; }
}

/// <summary>A stored file opened for download.</summary>
public record VehicleDocumentContent(System.IO.Stream Content, string ContentType, string FileName);
