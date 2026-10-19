namespace PMS.Application.Models.Operations;

/// <summary>Book a slot picked on the 3D model (FR-RSV-02).</summary>
public class CreateReservationModel
{
    public int ParkingLotId { get; set; }

    public int SlotId { get; set; }

    public Guid VehicleId { get; set; }

    /// <summary>Expected arrival; at most <see cref="ReservationRules.MaxLeadHours"/> ahead (BR-01). An offset is honoured; without one UTC is assumed.</summary>
    public DateTimeOffset ArrivalTime { get; set; }
}

/// <summary>Change the arrival time and/or the slot before the change deadline (FR-RSV-03).</summary>
public class UpdateReservationModel
{
    public DateTimeOffset? ArrivalTime { get; set; }

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

    public int? ZoneId { get; set; }

    public string ZoneName { get; set; }

    public int? SlotId { get; set; }

    public string SlotCode { get; set; }

    public Guid VehicleId { get; set; }

    public string PlateNumber { get; set; }

    public DateTime ArrivalTime { get; set; }

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

/// <summary>Signed reservation QR (FR-RSV-04, NFR-16); the app renders <see cref="QrPayload"/> as a QR code.</summary>
public class ReservationQrModel
{
    public long ReservationId { get; set; }

    public string Code { get; set; }

    public string QrPayload { get; set; }

    public DateTime ValidFrom { get; set; }

    public DateTime ValidUntil { get; set; }
}
