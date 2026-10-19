using System;
using System.Collections.Generic;
using PMS.Application.Helpers.VehicleDocuments;

namespace PMS.Application.Models.VehicleDocuments;

/// <summary>Document kinds and sides accepted by the upload endpoint, and the check's statuses.</summary>
public static class VehicleDocumentKinds
{
    public const string RegistrationCertificate = VehicleDocumentVerifier.RegistrationCertificate;
    public const string CitizenId = VehicleDocumentVerifier.CitizenId;
    public const string Front = "Front";
    public const string Back = "Back";

    public static readonly string[] Types = [RegistrationCertificate, CitizenId];
    public static readonly string[] Sides = [Front, Back];
}

public static class VehicleVerificationStatus
{
    /// <summary>Files are being uploaded; nothing was submitted yet.</summary>
    public const string Draft = "Draft";
    /// <summary>Submitted; the OCR job is queued.</summary>
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string AutoVerified = "AutoVerified";
    /// <summary>Waiting for Staff with the "Xác minh phương tiện" permission (FR-VER-03).</summary>
    public const string NeedsReview = "NeedsReview";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
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
    /// <summary>Draft, Pending, Processing, AutoVerified, NeedsReview, Approved or Rejected.</summary>
    public string Status { get; set; }
    public string StatusReason { get; set; }
    /// <summary>Lowest field confidence (0..1), null until OCR ran.</summary>
    public double? Confidence { get; set; }
    public List<FieldCheck> Fields { get; set; } = [];
    public List<VehicleDocumentFileModel> Documents { get; set; } = [];
    /// <summary>"RegistrationCertificate/Back"-style entries still needed before the check can be submitted.</summary>
    public List<string> MissingDocuments { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string ReviewNote { get; set; }
    /// <summary>When the photos will be (or were) deleted under BR-26.</summary>
    public DateTime? FilesDeleteAt { get; set; }
    public DateTime? FilesDeletedAt { get; set; }
}

/// <summary>A check in the Staff review queue, with the data it was compared against.</summary>
public class VehicleVerificationReviewModel : VehicleVerificationModel
{
    public string CustomerName { get; set; }
    public string CustomerCitizenId { get; set; }
    public string VehicleType { get; set; }
    public string Brand { get; set; }
    public string Model { get; set; }
    public string Color { get; set; }
    public string OwnerCitizenId { get; set; }
}

/// <summary>Staff decision on a check that needed review.</summary>
public class ReviewVehicleVerificationModel
{
    public bool Approve { get; set; }

    /// <summary>Required when rejecting: the reason shown to the customer.</summary>
    public string Note { get; set; }
}

/// <summary>A stored file opened for download.</summary>
public record VehicleDocumentContent(System.IO.Stream Content, string ContentType, string FileName);
