namespace PMS.Application.Models.Operations;

/// <summary>Book a slot picked on the 3D model (FR-RSV-02).</summary>
public class CreateReservationModel
{
    public int ParkingLotId { get; set; }

    public int SlotId { get; set; }

    public Guid VehicleId { get; set; }

    /// <summary>Expected arrival: in the future and at most <see cref="ReservationRules.MaxLeadDays"/> days ahead. An offset is honoured; without one UTC is assumed.</summary>
    public DateTimeOffset ArrivalTime { get; set; }

    /// <summary>"Scheduled" (with <see cref="DepartureTime"/>) or "PayAsYouGo" (default: leave whenever).</summary>
    public string BookingMode { get; set; } = Operations.BookingMode.PayAsYouGo;

    /// <summary>Planned departure of a Scheduled booking; ignored for pay-as-you-go.</summary>
    public DateTimeOffset? DepartureTime { get; set; }
}

/// <summary>Change the arrival time and/or the slot before the change deadline (FR-RSV-03).</summary>
public class UpdateReservationModel
{
    public DateTimeOffset? ArrivalTime { get; set; }

    /// <summary>Switch between "Scheduled" and "PayAsYouGo"; null keeps the current mode.</summary>
    public string BookingMode { get; set; }

    /// <summary>New planned departure (Scheduled only).</summary>
    public DateTimeOffset? DepartureTime { get; set; }


    public int? SlotId { get; set; }
}

public class CancelReservationModel
{
    public string Reason { get; set; }
}

public class ReservationResponseModel
{
    public long Id { get; set; }

    public string Code { get; set; }

    public string Status { get; set; }

    public int ParkingLotId { get; set; }

    public string ParkingLotName { get; set; }

    public string ParkingLotAddress { get; set; }

    public string ParkingLotDistrict { get; set; }

    public int? ZoneId { get; set; }

    public string ZoneName { get; set; }

    public int? SlotId { get; set; }

    public string SlotCode { get; set; }

    public Guid VehicleId { get; set; }

    public string PlateNumber { get; set; }

    /// <summary>Brand and model of the booked vehicle, e.g. "Toyota Camry".</summary>
    public string VehicleLabel { get; set; }

    public DateTime ArrivalTime { get; set; }

    /// <summary>"Scheduled" or "PayAsYouGo".</summary>
    public string BookingMode { get; set; }

    /// <summary>Planned departure of a Scheduled booking (null for pay-as-you-go).</summary>
    public DateTime? DepartureTime { get; set; }

    /// <summary>The bay is marked Reserved for this booking now (it is locked shortly before arrival).</summary>
    public bool IsSlotHeld { get; set; }

    /// <summary>BR-02: check-in window.</summary>
    public DateTime CheckInFrom { get; set; }

    public DateTime CheckInUntil { get; set; }

    public decimal DepositAmount { get; set; }

    /// <summary>Pay the deposit before this or the reservation expires (BR-06).</summary>
    public DateTime? DepositDueAt { get; set; }

    public decimal RefundAmount { get; set; }

    public string CancelReason { get; set; }

    public string CancelledBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    /// <summary>The arrival time / slot can still be changed (FR-RSV-03).</summary>
    public bool CanChange { get; set; }

    public bool CanCancel { get; set; }

    /// <summary>What a cancellation now would refund (BR-04).</summary>
    public decimal RefundIfCancelledNow { get; set; }

    /// <summary>The open deposit payment, if one was started.</summary>
    public PaymentResponseModel PendingPayment { get; set; }
}

/// <summary>
/// What the entry lane shows once the Staff scans a reservation QR or types its code: the booking, the
/// booked vehicle (to fill the plate and vehicle type) and whether it can check in right now.
/// </summary>
public class GateReservationLookupModel
{
    public ReservationResponseModel Reservation { get; set; }

    public int VehicleTypeId { get; set; }

    public string VehicleTypeName { get; set; }

    public string VehicleBrand { get; set; }

    public string VehicleModel { get; set; }

    public string VehicleColor { get; set; }

    public string CustomerName { get; set; }

    /// <summary>True when the booking is confirmed, inside its check-in window and the vehicle is not parked anywhere.</summary>
    public bool CanCheckIn { get; set; }

    /// <summary>Why it cannot check in (null when it can).</summary>
    public string Problem { get; set; }

    /// <summary>Open session of the booked vehicle, if it is already inside a lot.</summary>
    public string OpenSessionCode { get; set; }
}

/// <summary>Signed reservation QR (FR-RSV-04, NFR-16); the app renders <see cref="QrPayload"/> as a QR code.</summary>
public class ReservationQrModel
{
    public long ReservationId { get; set; }

    public string Code { get; set; }

    public string QrPayload { get; set; }

    public DateTime ValidFrom { get; set; }

    public DateTime ValidUntil { get; set; }
}
