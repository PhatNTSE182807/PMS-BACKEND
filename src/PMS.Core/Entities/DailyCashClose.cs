using System;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// One cash drawer of a Staff member at a lot: the float it opens with and the cash total handed in at the close
/// (float + cash taken + approved top-ups). Since 2026-11-15 nothing is counted by banknote, so new rows leave
/// FloatJson, CountedJson, CountedTotal and Difference empty; older rows keep their count. Closing a drawer opens
/// the next one (CloseNo 2, 3…). Never deleted: a Manager confirms it.
/// </summary>
public partial class DailyCashClose
{
    public long Id { get; set; }

    public int ParkingLotId { get; set; }

    public string StaffUserId { get; set; }

    /// <summary>The lot's local date the drawer belongs to.</summary>
    public DateOnly BusinessDate { get; set; }

    /// <summary>1 for the day's first drawer; higher for extra closes after cash taken later that day.</summary>
    public int CloseNo { get; set; }

    /// <summary>Open, Closed, HasDifference or Confirmed (CashCloseStatus).</summary>
    public string Status { get; set; }

    public DateTime OpenedAt { get; set; }

    /// <summary>When the drawer must be closed: 23:00 of the business day, or sooner when the Admin's simulation ends the day.</summary>
    public DateTime? CloseDueAt { get; set; }

    /// <summary>When the drawer was found still open after its deadline and the Manager was told.</summary>
    public DateTime? OverdueAt { get; set; }

    /// <summary>Legacy: banknotes put in the drawer when it opened (rows before 2026-11-15).</summary>
    public string FloatJson { get; set; }

    public decimal FloatTotal { get; set; }

    /// <summary>Legacy: banknotes the Staff counted at the close (rows before 2026-11-15).</summary>
    public string CountedJson { get; set; }

    public decimal? CountedTotal { get; set; }

    /// <summary>The cash total handed in: float + cash taken by this Staff + approved top-ups; set at the close.</summary>
    public decimal? ExpectedTotal { get; set; }

    /// <summary>Legacy: counted minus expected (negative: cash was missing).</summary>
    public decimal? Difference { get; set; }

    public string DifferenceReason { get; set; }

    public DateTime? ClosedAt { get; set; }

    public string ConfirmedBy { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    public string ConfirmNote { get; set; }
}
