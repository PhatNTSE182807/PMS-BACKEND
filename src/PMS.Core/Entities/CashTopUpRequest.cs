using System;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// A Staff member's drawer is running out of cash (usually change): they ask for an amount and the lot's Manager
/// approves or rejects it. Approved cash counts as put into that drawer, so its cash total includes it. Never deleted.
/// </summary>
public class CashTopUpRequest
{
    public long Id { get; set; }

    public int ParkingLotId { get; set; }

    /// <summary>The open drawer (DailyCashCloses.Id) the cash goes into.</summary>
    public long DailyCashCloseId { get; set; }

    /// <summary>Staff who asked (dbo.Users.Id).</summary>
    public string StaffUserId { get; set; }

    /// <summary>Legacy: banknotes asked for (rows before 2026-11-15); "{}" since amounts are asked for.</summary>
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
