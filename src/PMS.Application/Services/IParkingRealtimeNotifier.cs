namespace PMS.Application.Services;

/// <summary>
/// Real-time pushes of reservations, gate sessions, payments and zone counters (SignalR in PMS.API).
/// Zone counters also go to the public lot channel (LotHub, no plates); sessions and alerts only go to
/// the internal channel of the lot's Staff and Managers (SRS FR-3D-09); reservations and payments to
/// the customer who owns them.
/// </summary>
public interface IParkingRealtimeNotifier
{
    Task ReservationChangedAsync(string userId, long reservationId, int lotId, string status, int? slotId);

    /// <summary>FR-RSV-10: "arriving soon" (kind = "Arrival") or "hold ends soon" (kind = "HoldEnding").</summary>
    Task ReservationReminderAsync(string userId, long reservationId, string kind, DateTime atUtc);

    Task PaymentChangedAsync(string userId, int? lotId, long paymentId, string status, string purpose, long? reservationId, long? sessionId);

    Task SessionChangedAsync(int lotId, long sessionId, string status, string plateNumber);

    Task ZoneCounterChangedAsync(int lotId, int zoneId, int availableSpaces, int occupiedSpaces, int totalCapacity);

    /// <summary>An alert for the lot's Staff/Managers (overdue vehicle, failed refund, blocked gate...).</summary>
    Task OpsAlertAsync(int lotId, string code, string message, long? sessionId);
}
