namespace PMS.Application.Models.Operations;

/// <summary>Opens the Staff's cash drawer with the cash they start with (the float).</summary>
public class OpenCashDrawerModel
{
    /// <summary>The float in VND; the standard float (8,880,000 VND) when left out.</summary>
    public decimal? Float { get; set; }
}

public class ConfirmCashCloseModel
{
    /// <summary>Optional; required only for an older close that was counted with a difference.</summary>
    public string Note { get; set; }
}

/// <summary>A Staff cash drawer: the float, the cash taken and the running cash total.</summary>
public class CashDrawerModel
{
    /// <summary>Null when no drawer is open yet today (Status NotOpened).</summary>
    public long? Id { get; set; }

    public int ParkingLotId { get; set; }

    public string StaffUserId { get; set; }

    public string StaffName { get; set; }

    public DateOnly BusinessDate { get; set; }

    public int CloseNo { get; set; }

    /// <summary>NotOpened, Open, Closed or Confirmed (HasDifference only on drawers closed with a count, before 2026-11-15).</summary>
    public string Status { get; set; }

    public DateTime? OpenedAt { get; set; }

    public decimal FloatTotal { get; set; }

    /// <summary>Cash fees this Staff recorded while the drawer was open (the change given back is not kept).</summary>
    public decimal CashCollected { get; set; }

    public int CashPayments { get; set; }

    /// <summary>Cash in the drawer: float + cash collected + approved top-ups. Live while open, frozen at the close.</summary>
    public decimal CashTotal { get; set; }

    /// <summary>Only on drawers closed with a banknote count (before 2026-11-15).</summary>
    public decimal? CountedTotal { get; set; }

    /// <summary>Counted − expected on those older drawers; negative when cash was missing.</summary>
    public decimal? Difference { get; set; }

    public string DifferenceReason { get; set; }

    public DateTime? ClosedAt { get; set; }

    public string ConfirmedBy { get; set; }

    public string ConfirmedByName { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    public string ConfirmNote { get; set; }

    /// <summary>The standard float a drawer opened by the system starts with, in VND.</summary>
    public decimal DefaultFloat { get; set; }

    /// <summary>Cash the Manager approved into this drawer (already in CashTotal).</summary>
    public decimal TopUpTotal { get; set; }

    /// <summary>Every request for more cash made for this drawer, newest first.</summary>
    public List<CashTopUpModel> TopUps { get; set; } = [];
}

/// <summary>The Staff asks the Manager for cash to add to their open drawer.</summary>
public class RequestCashTopUpModel
{
    /// <summary>The amount in VND.</summary>
    public decimal Amount { get; set; }

    /// <summary>Optional, e.g. "Small notes for change: 10,000 and 20,000".</summary>
    public string Reason { get; set; }
}

public class DecideCashTopUpModel
{
    /// <summary>Optional when approving, required when rejecting.</summary>
    public string Note { get; set; }
}

/// <summary>A request for more cash in a Staff cash drawer.</summary>
public class CashTopUpModel
{
    public long Id { get; set; }

    public int ParkingLotId { get; set; }

    public long DrawerId { get; set; }

    public int CloseNo { get; set; }

    public string StaffUserId { get; set; }

    public string StaffName { get; set; }

    public decimal Total { get; set; }

    public string Reason { get; set; }

    /// <summary>Pending, Approved, Rejected or Cancelled.</summary>
    public string Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public string DecidedBy { get; set; }

    public string DecidedByName { get; set; }

    public DateTime? DecidedAt { get; set; }

    public string DecisionNote { get; set; }

    /// <summary>The drawer is still open, so the cash can still go into it.</summary>
    public bool DrawerOpen { get; set; }
}
