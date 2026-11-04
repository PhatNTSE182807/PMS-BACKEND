using System;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// A Staff member's drawer is running out of banknotes (usually change): they list the notes they need and
/// the lot's Manager approves or rejects the list. Approved notes count as cash put into that drawer, so the
/// expected cash at the close includes them. Never deleted.
/// </summary>
public class CashTopUpRequest
{
    public long Id { get; set; }

    public int ParkingLotId { get; set; }

    /// <summary>The open drawer (DailyCashCloses.Id) the notes go into.</summary>
    public long DailyCashCloseId { get; set; }

    /// <summary>Staff who asked (dbo.Users.Id).</summary>
    public string StaffUserId { get; set; }

    /// <summary>Banknotes asked for, as JSON {"50000": 10, "10000": 20, …}.</summary>
    public string NotesJson { get; set; }

    public decimal Total { get; set; }

    /// <summary>Why the Staff needs the cash (optional).</summary>
    public string Reason { get; set; }

    /// <summary>Pending, Approved, Rejected or Cancelled (CashTopUpStatus).</summary>
    public string Status { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Manager who approved or rejected it (or the Staff, when cancelled).</summary>
    public string DecidedBy { get; set; }

    public DateTime? DecidedAt { get; set; }

    /// <summary>The Manager's note; required when rejecting.</summary>
    public string DecisionNote { get; set; }
}
