namespace PMS.Application.Models.Operations;

/// <summary>How a booking ends: at a planned time, or whenever the driver leaves.</summary>
public static class BookingMode
{
    /// <summary>Arrival and departure picked; the bay is booked for that window.</summary>
    public const string Scheduled = "Scheduled";
    /// <summary>Arrival picked, departure open; the fee is counted until the vehicle leaves.</summary>
    public const string PayAsYouGo = "PayAsYouGo";

    public static readonly string[] All = [Scheduled, PayAsYouGo];
}

/// <summary>Reservation states (SRS 4.3.3).</summary>
public static class ReservationStatus
{
    /// <summary>Chờ cọc: the slot is held until the deposit deadline.</summary>
    public const string PendingDeposit = "PendingDeposit";
    /// <summary>Đã xác nhận: deposit paid, QR issued.</summary>
    public const string Confirmed = "Confirmed";
    /// <summary>Đã check-in: the vehicle entered on this reservation.</summary>
    public const string CheckedIn = "CheckedIn";
    /// <summary>Hoàn tất: the vehicle left; the deposit was deducted from the fee.</summary>
    public const string Completed = "Completed";
    /// <summary>Hủy: cancelled by the customer (refund per BR-04).</summary>
    public const string Cancelled = "Cancelled";
    /// <summary>Bãi hủy: cancelled by the lot (100 % refund, FR-RSV-09).</summary>
    public const string LotCancelled = "LotCancelled";
    /// <summary>Hết hạn: the deposit was not paid in time (or the customer gave up).</summary>
    public const string Expired = "Expired";
    /// <summary>No-show: not checked in before the hold ended; the deposit is kept (BR-05).</summary>
    public const string NoShow = "NoShow";

    /// <summary>
    /// Bookings still in progress (BR-07 limits): waiting for the deposit, confirmed, or the vehicle is
    /// parked on it. A checked-in booking counts until the vehicle leaves, so a parked car cannot book again.
    /// </summary>
    public static readonly string[] Active = [PendingDeposit, Confirmed, CheckedIn];
}

/// <summary>Parking session states (SRS 4.3.2) plus the two pre-entry states before the sensor confirms passage.</summary>
public static class SessionStatus
{
    /// <summary>The barrier opened; waiting for the loop sensor. Not a valid session yet.</summary>
    public const string PendingEntry = "PendingEntry";
    /// <summary>Đang đỗ.</summary>
    public const string Active = "Active";
    /// <summary>Chờ duyệt mất vé.</summary>
    public const string LostTicketPending = "LostTicketPending";
    /// <summary>Chờ thanh toán: a fee was requested at a payment point.</summary>
    public const string PendingPayment = "PendingPayment";
    /// <summary>Đã thanh toán (or the fee is 0 VND).</summary>
    public const string Paid = "Paid";
    /// <summary>Hoàn tất: the vehicle passed the exit barrier.</summary>
    public const string Completed = "Completed";
    /// <summary>Đóng bất thường (ghost session closed by a Manager).</summary>
    public const string AbnormalClosed = "AbnormalClosed";
    /// <summary>Ra khẩn cấp.</summary>
    public const string EmergencyExit = "EmergencyExit";
    /// <summary>The vehicle turned around before the sensor; never counted.</summary>
    public const string Cancelled = "Cancelled";

    /// <summary>Sessions that block another entry with the same plate (FR-OPS-11, NFR-14).</summary>
    public static readonly string[] Open = [PendingEntry, Active, LostTicketPending, PendingPayment, Paid];

    /// <summary>The vehicle is inside the lot.</summary>
    public static readonly string[] Inside = [Active, LostTicketPending, PendingPayment, Paid];
}

/// <summary>Slot states (SRS 4.3.1); the names match SlotService.</summary>
public static class SlotStatus
{
    public const string Available = "Available";
    public const string Reserved = "Reserved";
    public const string Occupied = "Occupied";
    public const string Maintenance = "Maintenance";
}

public static class SessionType
{
    public const string WalkIn = "WalkIn";
    public const string Reservation = "Reservation";
    public const string Whitelist = "Whitelist";
}

public static class PaymentStatus
{
    public const string Pending = "Pending";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Expired = "Expired";
    /// <summary>A refund the gateway has not settled yet, or that failed and waits for a retry / Manager (FR-PAY-10).</summary>
    public const string RefundPending = "RefundPending";
    public const string Refunded = "Refunded";
}

public static class PaymentPurpose
{
    public const string Deposit = "Deposit";
    public const string ParkingFee = "ParkingFee";
    public const string Refund = "Refund";
}

public static class PaymentMethod
{
    public const string Cash = "Cash";
    public const string Online = "Online";
}

public static class PaymentChannel
{
    /// <summary>Customer app: opens an Exit Window.</summary>
    public const string App = "App";
    /// <summary>Counter or kiosk: opens an Exit Window.</summary>
    public const string Kiosk = "Kiosk";
    /// <summary>At the exit lane: the barrier opens right away, no Exit Window.</summary>
    public const string Lane = "Lane";
}

public static class PaymentProviders
{
    public const string VnPay = "VNPay";
    public const string MoMo = "MoMo";
}

public static class PlateListType
{
    public const string Whitelist = "Whitelist";
    public const string Blacklist = "Blacklist";
}
