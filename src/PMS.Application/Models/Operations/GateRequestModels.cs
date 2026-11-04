namespace PMS.Application.Models.Operations;

/// <summary>What the customer asked for at the gate.</summary>
public static class GateRequestKind
{
    /// <summary>Arriving on a confirmed booking: open the entry barrier.</summary>
    public const string CheckIn = "CheckIn";
    /// <summary>Leaving with a parked vehicle: open the exit barrier.</summary>
    public const string CheckOut = "CheckOut";

    public static readonly string[] All = [CheckIn, CheckOut];
}

public static class GateRequestStatus
{
    /// <summary>In the lot's queue, waiting for a Staff member at the lane.</summary>
    public const string Waiting = "Waiting";
    /// <summary>Staff opened the barrier (check-in created the session / check-out closed it).</summary>
    public const string Completed = "Completed";
    /// <summary>Staff did not open the barrier; <c>Note</c> says why.</summary>
    public const string Refused = "Refused";
    /// <summary>The customer withdrew it, or it no longer applies (booking or session ended elsewhere).</summary>
    public const string Cancelled = "Cancelled";
}

/// <summary>A check-in or check-out request, with everything the Staff needs to decide at the lane.</summary>
public class GateRequestModel
{
    public long Id { get; set; }

    public string Kind { get; set; }

    public string Status { get; set; }

    public int ParkingLotId { get; set; }

    public string LotName { get; set; }

    public string PlateNumber { get; set; }

    /// <summary>Brand and model, e.g. "Toyota Camry".</summary>
    public string VehicleLabel { get; set; }

    public string VehicleColor { get; set; }

    /// <summary>Colour the 3D gate cameras and lot map paint this vehicle: registered, or picked from the plate (see VehiclePaint).</summary>
    public string PaintColor { get; set; }

    public int? VehicleTypeId { get; set; }

    public string VehicleTypeName { get; set; }

    public string CustomerName { get; set; }

    public string CustomerPhone { get; set; }

    public long? ReservationId { get; set; }

    public string ReservationCode { get; set; }

    public string ReservationStatus { get; set; }

    public DateTime? ArrivalTime { get; set; }

    public DateTime? CheckInFrom { get; set; }

    public DateTime? CheckInUntil { get; set; }

    public string SlotCode { get; set; }

    public long? SessionId { get; set; }

    public string SessionCode { get; set; }

    public string SessionStatus { get; set; }

    public DateTime? EntryTime { get; set; }

    /// <summary>Paid so far on the session (check-out).</summary>
    public decimal PaidAmount { get; set; }

    /// <summary>Paid: the vehicle may leave until then (BR-10).</summary>
    public DateTime? ExitWindowEndsAt { get; set; }

    /// <summary>
    /// Why the barrier would not open right now (booking outside its window, fee still due...), or null.
    /// Staff can still try; the gate rules decide.
    /// </summary>
    public string Problem { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? HandledAt { get; set; }

    public string Note { get; set; }
}

/// <summary>Staff decision options when opening the barrier for a request.</summary>
public class ApproveGateRequestModel
{
    public int? GateId { get; set; }

    /// <summary>The Staff checked the plate against the camera (clears LOW_CONFIDENCE / photo checks).</summary>
    public bool PlateConfirmed { get; set; } = true;

    /// <summary>Let the vehicle in although the zone is full (BR-19).</summary>
    public bool AllowWhenFull { get; set; }

    /// <summary>Accept a changed vehicle / mismatching exit plate.</summary>
    public bool AllowPlateMismatch { get; set; }

    /// <summary>Required whenever an override is used; kept in the session log.</summary>
    public string OverrideReason { get; set; }
}

/// <summary>Staff refused the request; the reason is shown to the customer.</summary>
public class RefuseGateRequestModel
{
    public string Reason { get; set; }
}

/// <summary>
/// Outcome of "Open barrier": the request (Completed when the barrier opened, still Waiting when blocked)
/// with the gate result. Check-in leaves the session PendingEntry until the Staff confirms the vehicle passed.
/// </summary>
public class GateRequestDecisionModel
{
    public GateRequestModel Request { get; set; }

    public bool BarrierOpen { get; set; }

    public CheckInResultModel CheckIn { get; set; }

    public CheckoutResultModel CheckOut { get; set; }

    /// <summary>Blocking alerts the Staff must clear (or refuse the request).</summary>
    public List<GateAlertModel> Alerts { get; set; } = [];
}
