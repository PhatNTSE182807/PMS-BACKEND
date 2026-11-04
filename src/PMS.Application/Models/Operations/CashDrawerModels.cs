namespace PMS.Application.Models.Operations;

/// <summary>Opens the Staff's cash drawer for the day with the banknotes counted in (the float).</summary>
public class OpenCashDrawerModel
{
    /// <summary>Banknote value → how many, e.g. {"50000": 10, "10000": 20}.</summary>
    public Dictionary<int, int> Float { get; set; } = [];
}

/// <summary>Closes the drawer with the banknotes the Staff counted (blind: the expected cash is not shown first).</summary>
public class CloseCashDrawerModel
{
    public Dictionary<int, int> Counted { get; set; } = [];

    /// <summary>Required when the count is more than the threshold off the expected cash.</summary>
    public string Reason { get; set; }
}

/// <summary>Admin simulation: ends the business day of one lot (or all) now; open drawers must close within the grace period.</summary>
public class EndBusinessDayModel
{
    /// <summary>Null ends the day in every lot.</summary>
    public int? LotId { get; set; }

    /// <summary>Seconds the Staff get to count and close (5 to 3600).</summary>
    public int GraceSeconds { get; set; } = 30;
}

public class EndBusinessDayResultModel
{
    /// <summary>Open drawers that now have to close.</summary>
    public int DrawersToClose { get; set; }

    public DateTime CloseDueAt { get; set; }
}

public class ConfirmCashCloseModel
{
    /// <summary>Required when the close has a difference over the threshold.</summary>
    public string Note { get; set; }
}

/// <summary>A Staff cash drawer of one business day: float, cash taken, expected and counted banknotes.</summary>
public class CashDrawerModel
{
    /// <summary>Null when no drawer is open yet today (Status NotOpened).</summary>
    public long? Id { get; set; }

    public int ParkingLotId { get; set; }

    public string StaffUserId { get; set; }

    public string StaffName { get; set; }

    public DateOnly BusinessDate { get; set; }

    public int CloseNo { get; set; }

    /// <summary>NotOpened, Open, Closed, HasDifference or Confirmed.</summary>
    public string Status { get; set; }

    public DateTime? OpenedAt { get; set; }

    /// <summary>Deadline to close the drawer (23:00 of the business day, or sooner in a simulated day end).</summary>
    public DateTime? CloseDueAt { get; set; }

    /// <summary>Still open after its deadline; the Manager was told.</summary>
    public bool IsOverdue { get; set; }

    /// <summary>Closed after its deadline.</summary>
    public bool ClosedLate { get; set; }

    public Dictionary<int, int> Float { get; set; } = [];

    public decimal FloatTotal { get; set; }

    /// <summary>Cash fees this Staff recorded while the drawer was open.</summary>
    public decimal CashCollected { get; set; }

    public int CashPayments { get; set; }

    /// <summary>Banknotes that should be in the drawer: float + notes received − change given (payments entered note by note).</summary>
    public Dictionary<int, int> ExpectedNotes { get; set; } = [];

    /// <summary>Float + cash collected.</summary>
    public decimal ExpectedTotal { get; set; }

    /// <summary>Cash recorded as an amount only (not note by note), so it is in the total but not in ExpectedNotes.</summary>
    public decimal UntrackedCash { get; set; }

    public Dictionary<int, int> Counted { get; set; }

    public decimal? CountedTotal { get; set; }

    /// <summary>Counted − expected; negative when cash is missing.</summary>
    public decimal? Difference { get; set; }

    public string DifferenceReason { get; set; }

    public DateTime? ClosedAt { get; set; }

    public string ConfirmedBy { get; set; }

    public string ConfirmedByName { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    public string ConfirmNote { get; set; }

    /// <summary>A difference larger than this needs a reason and a Manager's confirmation (BR-12).</summary>
    public decimal DifferenceThreshold { get; set; }

    /// <summary>Banknote values the drawer counts, largest first.</summary>
    public IReadOnlyList<int> Denominations { get; set; } = [];

    /// <summary>Cash the Manager approved into this drawer (already in ExpectedTotal and ExpectedNotes).</summary>
    public decimal TopUpTotal { get; set; }

    /// <summary>Every request for more banknotes made for this drawer, newest first.</summary>
    public List<CashTopUpModel> TopUps { get; set; } = [];
}

/// <summary>The Staff asks the Manager for banknotes to add to their open drawer.</summary>
public class RequestCashTopUpModel
{
    /// <summary>Banknote value → how many, e.g. {"10000": 20, "5000": 20}.</summary>
    public Dictionary<int, int> Notes { get; set; } = [];

    /// <summary>Optional, e.g. "Out of small notes for change".</summary>
    public string Reason { get; set; }
}

public class DecideCashTopUpModel
{
    /// <summary>Optional when approving, required when rejecting.</summary>
    public string Note { get; set; }
}

/// <summary>A request for more banknotes in a Staff cash drawer.</summary>
public class CashTopUpModel
{
    public long Id { get; set; }

    public int ParkingLotId { get; set; }

    public long DrawerId { get; set; }

    public int CloseNo { get; set; }

    public string StaffUserId { get; set; }

    public string StaffName { get; set; }

    public Dictionary<int, int> Notes { get; set; } = [];

    public decimal Total { get; set; }

    public string Reason { get; set; }

    /// <summary>Pending, Approved, Rejected or Cancelled.</summary>
    public string Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public string DecidedBy { get; set; }

    public string DecidedByName { get; set; }

    public DateTime? DecidedAt { get; set; }

    public string DecisionNote { get; set; }

    /// <summary>The drawer is still open, so the notes can still go into it.</summary>
    public bool DrawerOpen { get; set; }
}
