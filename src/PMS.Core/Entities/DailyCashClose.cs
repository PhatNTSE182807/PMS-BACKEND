using System;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// One cash drawer of a Staff member at a lot for a business day (BR-12): the float counted in when it opens,
/// the banknotes counted out when it closes, the expected cash and the difference. Cash taken after a close
/// opens another drawer of the same day (CloseNo 2, 3…). Never deleted: a Manager confirms it.
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

    /// <summary>When the drawer must be closed: 23:00 of the business day (BR-12), or sooner when the Admin's simulation ends the day.</summary>
    public DateTime? CloseDueAt { get; set; }

    /// <summary>When the drawer was found still open after its deadline and the Manager was told.</summary>
    public DateTime? OverdueAt { get; set; }

    /// <summary>Banknotes put in the drawer when it opened, as JSON {"500000": 1, "10000": 20, …}.</summary>
    public string FloatJson { get; set; }

    public decimal FloatTotal { get; set; }

    /// <summary>Banknotes the Staff counted at the close, same JSON shape; null while open.</summary>
    public string CountedJson { get; set; }

    public decimal? CountedTotal { get; set; }

    /// <summary>Float plus the cash taken by this Staff while the drawer was open.</summary>
    public decimal? ExpectedTotal { get; set; }

    /// <summary>Counted minus expected (negative: cash is missing).</summary>
    public decimal? Difference { get; set; }

    public string DifferenceReason { get; set; }

    public DateTime? ClosedAt { get; set; }

    public string ConfirmedBy { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    public string ConfirmNote { get; set; }
}
