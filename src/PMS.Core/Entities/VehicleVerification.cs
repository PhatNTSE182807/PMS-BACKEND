using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// One check of a vehicle's papers (SRS FR-VER-01..04): the registration certificate (cà vẹt) and the
/// uploader's Citizen ID card (CCCD), read by OCR and compared with the vehicle and the user's profile.
/// Draft while files are uploaded, Pending/Processing while the OCR job runs, then AutoVerified,
/// NeedsReview (Staff decides), Approved or Rejected.
/// </summary>
public partial class VehicleVerification
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid VehicleId { get; set; }

    public int CustomerId { get; set; }

    /// <summary>Identity user who uploaded the papers; the only customer allowed to see them.</summary>
    public string UserId { get; set; }

    /// <summary>Draft, Pending, Processing, AutoVerified, NeedsReview, Approved or Rejected.</summary>
    public string Status { get; set; }

    /// <summary>Per-field OCR comparison as JSON (list of VehicleDocumentFieldResultModel).</summary>
    public string ResultJson { get; set; }

    /// <summary>Lowest confidence among the compared fields (0..1); null until OCR ran.</summary>
    public decimal? Confidence { get; set; }

    /// <summary>Why the check did not auto-verify, or why it failed (shown to the user and Staff).</summary>
    public string StatusReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? SubmittedAt { get; set; }

    public DateTime? ProcessedAt { get; set; }

    /// <summary>Staff who approved or rejected a check that needed review.</summary>
    public string ReviewedBy { get; set; }

    public string ReviewNote { get; set; }

    /// <summary>When the outcome became final (auto or Staff); photos are deleted 30 days later (BR-26).</summary>
    public DateTime? DecidedAt { get; set; }

    /// <summary>When the photos were deleted under BR-26; the row and its result are kept.</summary>
    public DateTime? FilesDeletedAt { get; set; }

    public virtual Vehicle Vehicle { get; set; }

    public virtual ICollection<VehicleDocument> Documents { get; set; } = new List<VehicleDocument>();
}
