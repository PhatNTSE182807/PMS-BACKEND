namespace PMS.Application.Models.Operations;

/// <summary>
/// A vehicle at an entry lane (FR-OPS-01). The lane gives the vehicle type; the plate comes from ANPR
/// (with its confidence) or is typed by the Staff. Overrides are the Staff's decisions on the alerts of a
/// previous attempt and need <see cref="OverrideReason"/>.
/// </summary>
public class CheckInModel
{
    public int? GateId { get; set; }

    public string PlateNumber { get; set; }

    /// <summary>The plate cannot be read or is missing: no ANPR, no duplicate-plate rule; ticket and photo only (FR-OPS-10).</summary>
    public bool NoPlate { get; set; }

    /// <summary>Required with <see cref="NoPlate"/>: why the plate is unreadable or missing. Not a device failure (that is <see cref="IsManual"/>).</summary>
    public string NoPlateReason { get; set; }

    /// <summary>Vehicle type of the lane; defaults to the reservation's vehicle.</summary>
    public int? VehicleTypeId { get; set; }

    /// <summary>ANPR confidence 0..1 (or 0..100); null when typed by hand.</summary>
    public decimal? AnprConfidence { get; set; }

    /// <summary>The Staff checked or corrected a low-confidence plate (FR-OPS-14).</summary>
    public bool PlateConfirmed { get; set; }

    /// <summary>Scanned reservation QR (signed payload).</summary>
    public string ReservationQr { get; set; }

    /// <summary>Reservation code typed by the Staff (phone flat, FR-RSV-04 exceptions).</summary>
    public string ReservationCode { get; set; }

    /// <summary>Enter as a walk-in even though a reservation matched (e.g. arrived early, 4.2.1).</summary>
    public bool IgnoreReservation { get; set; }

    /// <summary>Optional bay picked on the 3D model for a walk-in.</summary>
    public int? SlotId { get; set; }

    /// <summary>Device failure: entry recorded by hand (FR-OPS-03).</summary>
    public bool IsManual { get; set; }

    public string ManualReason { get; set; }

    public bool AllowBlacklisted { get; set; }

    public bool AllowWhenFull { get; set; }

    public bool AllowPlateMismatch { get; set; }

    public string OverrideReason { get; set; }

    public string ImageUrl { get; set; }
}

public class GateAlertModel
{
    /// <summary>LOT_CLOSED, PLATE_REQUIRED, LOW_CONFIDENCE, DUPLICATE_SESSION, BLACKLISTED, WHITELISTED, RESERVATION_INVALID,
    /// RESERVATION_OUTSIDE_WINDOW, RESERVATION_PLATE_MISMATCH, ZONE_FULL, SLOT_UNAVAILABLE.</summary>
    public string Code { get; set; }

    public string Message { get; set; }

    /// <summary>The barrier stays closed until this is resolved.</summary>
    public bool Blocking { get; set; }

    /// <summary>The Staff may let the vehicle in anyway with a reason.</summary>
    public bool CanOverride { get; set; }

    /// <summary>The other session for DUPLICATE_SESSION, so the Staff can look at it.</summary>
    public long? RelatedSessionId { get; set; }
}

public class CheckInResultModel
{
    /// <summary>"Created" (barrier opens, waiting for the sensor) or "Blocked".</summary>
    public string Outcome { get; set; }

    public bool BarrierOpen { get; set; }

    public List<GateAlertModel> Alerts { get; set; } = [];

    public SessionResponseModel Session { get; set; }

    /// <summary>Signed QR ticket for a Guest (lot code, entry time, lane, NFR-16).</summary>
    public string Ticket { get; set; }
}

public class SessionResponseModel
{
    public long Id { get; set; }

    public string Code { get; set; }

    public int ParkingLotId { get; set; }

    public string Status { get; set; }

    public string SessionType { get; set; }

    public string PlateNumber { get; set; }

    public bool HasPlate { get; set; }

    public int? VehicleTypeId { get; set; }

    public string VehicleTypeName { get; set; }

    /// <summary>Colour the 3D gate cameras and lot map paint this vehicle: registered, or picked from the plate (see VehiclePaint).</summary>
    public string PaintColor { get; set; }

    public DateTime EntryTime { get; set; }

    public DateTime? EntryConfirmedAt { get; set; }

    public int EntryGateId { get; set; }

    public int? ZoneId { get; set; }

    public string ZoneName { get; set; }

    public int? SlotId { get; set; }

    public string SlotCode { get; set; }

    public long? ReservationId { get; set; }

    public string ReservationCode { get; set; }

    public bool IsCustomerAccount { get; set; }

    public decimal PaidAmount { get; set; }

    public decimal FinalFee { get; set; }

    public DateTime? ExitWindowEndsAt { get; set; }

    public DateTime? ExitTime { get; set; }

    public bool IsManualEntry { get; set; }

    public string NoPlateReason { get; set; }

    public bool IsLostTicket { get; set; }

    /// <summary>Latest lost-ticket case: Pending, Approved or Rejected; null without a case (or an old one with no status).</summary>
    public string LostTicketStatus { get; set; }

    /// <summary>User id of who reported the latest lost-ticket case; that account cannot decide it.</summary>
    public string LostTicketReportedBy { get; set; }

    public DateTime? LostTicketReportedAt { get; set; }

    /// <summary>Why the ticket was reported lost (what the Staff checked).</summary>
    public string LostTicketReason { get; set; }

    /// <summary>Who has to decide the open case: Staff (papers uploaded), Manager, or Conditional once approved on the spot.</summary>
    public string LostTicketApprovalMode { get; set; }

    /// <summary>The Staff uploaded the Citizen ID and registration photos for the latest case.</summary>
    public bool LostTicketHasPapers { get; set; }

    /// <summary>The Manager's remarks or rejection reason.</summary>
    public string LostTicketDecisionNote { get; set; }

    public bool PlateMismatch { get; set; }

    public bool IsOverdue { get; set; }

    public string ZeroFeeReason { get; set; }

    public string OverrideNote { get; set; }
}

/// <summary>Ask for the fee at a payment point (exit lane, counter or app): the session becomes "Chờ thanh toán".</summary>
public class CheckoutQuoteRequestModel
{
    public int? GateId { get; set; }

    /// <summary>Plate read at the exit, compared with the entry plate (FR-OPS-04).</summary>
    public string PlateNumber { get; set; }

    public decimal? AnprConfidence { get; set; }
}

public class FeeDayModel
{
    public DateOnly Date { get; set; }

    public int Minutes { get; set; }

    public int Blocks { get; set; }

    public decimal Amount { get; set; }

    public bool Capped { get; set; }
}

public class CheckoutQuoteModel
{
    public SessionResponseModel Session { get; set; }

    public int DurationMinutes { get; set; }

    public decimal Fee { get; set; }

    public decimal LostTicketFee { get; set; }

    public decimal ExitWindowLateFee { get; set; }

    public decimal DepositApplied { get; set; }

    public decimal AlreadyPaid { get; set; }

    public decimal AmountDue { get; set; }

    /// <summary>Residual deposit refunded at check-out (BR-03).</summary>
    public decimal DepositRefund { get; set; }

    public string ZeroFeeReason { get; set; }

    /// <summary>Paid (or 0 VND) and the plates match: the barrier may open.</summary>
    public bool CanOpenBarrier { get; set; }

    public List<GateAlertModel> Alerts { get; set; } = [];

    public List<FeeDayModel> Days { get; set; } = [];

    /// <summary>PricingRule id/version, or null for the configured default tariff.</summary>
    public int? PricingRuleId { get; set; }

    public int? PricingRuleVersion { get; set; }
}

/// <summary>The vehicle passes the exit barrier.</summary>
public class CheckoutModel
{
    public int? GateId { get; set; }

    public string PlateNumber { get; set; }

    /// <summary>The Staff compared entry and exit photos (required for vehicles without a plate, FR-OPS-10).</summary>
    public bool PhotoChecked { get; set; }

    public bool AllowPlateMismatch { get; set; }

    public string OverrideReason { get; set; }

    public bool IsManual { get; set; }

    public string ManualReason { get; set; }
}

public class CheckoutResultModel
{
    public SessionResponseModel Session { get; set; }

    public bool BarrierOpen { get; set; }

    public decimal DepositRefund { get; set; }

    public List<GateAlertModel> Alerts { get; set; } = [];
}

/// <summary>Lost ticket with the driver's papers: the Staff photographs the Citizen ID and the registration certificate.</summary>
public class LostTicketPapersModel
{
    public string Reason { get; set; }

    public Stream CitizenIdPhoto { get; set; }

    public long CitizenIdLength { get; set; }

    public Stream RegistrationPhoto { get; set; }

    public long RegistrationLength { get; set; }
}

/// <summary>The Staff's decision after comparing the papers with the vehicle.</summary>
public class LostTicketReviewModel
{
    public bool Approve { get; set; }

    /// <summary>Required when refusing.</summary>
    public string Note { get; set; }
}

public class LostTicketModel
{
    public string Reason { get; set; }

    /// <summary>BR-13 conditions met (photos, papers and ID match; standard fine): no Manager approval needed.</summary>
    public bool ConditionsMet { get; set; }
}

public class SessionDecisionModel
{
    public string Reason { get; set; }
}

public class GateResponseModel
{
    public int Id { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }

    /// <summary>"In", "Out" or "Both".</summary>
    public string Direction { get; set; }
}

public class ZoneCounterModel
{
    public int ZoneId { get; set; }

    public string ZoneCode { get; set; }

    public string ZoneName { get; set; }

    public int? VehicleTypeId { get; set; }

    public int TotalCapacity { get; set; }

    public int OccupiedSpaces { get; set; }

    public int AvailableSpaces { get; set; }

    public bool IsFull => AvailableSpaces <= 0;
}

public class PlateListEntryModel
{
    public int Id { get; set; }

    public string ListType { get; set; }

    public string PlateNumber { get; set; }

    public int? ParkingLotId { get; set; }

    public string Reason { get; set; }

    public DateTime ExpiresAt { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }
}

public class CreatePlateListEntryModel
{
    /// <summary>"Whitelist" or "Blacklist".</summary>
    public string ListType { get; set; }

    public string PlateNumber { get; set; }

    public string Reason { get; set; }

    public DateTime ExpiresAt { get; set; }
}

public class StaffSessionHistoryPageModel
{
    public IReadOnlyList<SessionResponseModel> Items { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
}

/// <summary>One parking-fee payment taken at a lot (counter, exit lane, app or wallet).</summary>
public class StaffPaymentHistoryItemModel
{
    public long Id { get; set; }
    public string Code { get; set; }
    public DateTime? PaidAt { get; set; }
    public long? SessionId { get; set; }
    public string SessionCode { get; set; }
    public string PlateNumber { get; set; }
    /// <summary>Cash, Wallet, VNPay or MoMo.</summary>
    public string Method { get; set; }
    /// <summary>Lane, Kiosk (counter) or App.</summary>
    public string Channel { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; }
    /// <summary>The Staff who took the payment; null for online payments and for simulated vehicles (paid by the system).</summary>
    public string CollectedByName { get; set; }
    /// <summary>A walk-in sent by the Admin parking simulation: demo traffic, paid automatically at the exit lane.</summary>
    public bool IsSimulated { get; set; }
}

/// <summary>A page of a lot's payment history, newest first.</summary>
public class StaffPaymentHistoryPageModel
{
    public IReadOnlyList<StaffPaymentHistoryItemModel> Items { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
}
