using Microsoft.AspNetCore.SignalR;
using PMS.Application.Services;

namespace PMS.API.Hubs;

/// <summary>
/// Sends reservation, session, payment and counter changes. Zone counters also go to the public
/// <see cref="LotHub"/> group (numbers only); plates only ever go to the private <see cref="ParkingOpsHub"/>.
/// </summary>
public class SignalRParkingRealtimeNotifier(IHubContext<ParkingOpsHub> opsHub, IHubContext<LotHub> lotHub) : IParkingRealtimeNotifier
{
    public Task ReservationChangedAsync(string userId, long reservationId, int lotId, string status, int? slotId) =>
        Task.WhenAll(
            ToUser(userId, "ReservationChanged", new { reservationId, lotId, status, slotId }),
            opsHub.Clients.Group(ParkingOpsHub.OpsGroup(lotId)).SendAsync("ReservationChanged", new { reservationId, lotId, status, slotId }));

    public Task ReservationReminderAsync(string userId, long reservationId, string kind, DateTime atUtc) =>
        ToUser(userId, "ReservationReminder", new { reservationId, kind, at = DateTime.SpecifyKind(atUtc, DateTimeKind.Utc) });

    public Task PaymentChangedAsync(string userId, int? lotId, long paymentId, string status, string purpose, long? reservationId, long? sessionId)
    {
        var payload = new { paymentId, status, purpose, reservationId, sessionId };
        return Task.WhenAll(
            ToUser(userId, "PaymentChanged", payload),
            lotId == null ? Task.CompletedTask : opsHub.Clients.Group(ParkingOpsHub.OpsGroup(lotId.Value)).SendAsync("PaymentChanged", payload));
    }

    public Task SessionChangedAsync(int lotId, long sessionId, string status, string plateNumber) =>
        opsHub.Clients.Group(ParkingOpsHub.OpsGroup(lotId)).SendAsync("SessionChanged", new { lotId, sessionId, status, plateNumber });

    public Task ZoneCounterChangedAsync(int lotId, int zoneId, int availableSpaces, int occupiedSpaces, int totalCapacity)
    {
        var payload = new { lotId, zoneId, availableSpaces, occupiedSpaces, totalCapacity };
        return Task.WhenAll(
            lotHub.Clients.Group(LotHub.Group(lotId)).SendAsync("ZoneCounterChanged", payload),
            opsHub.Clients.Group(ParkingOpsHub.OpsGroup(lotId)).SendAsync("ZoneCounterChanged", payload));
    }

    public Task OpsAlertAsync(int lotId, string code, string message, long? sessionId) =>
        opsHub.Clients.Group(ParkingOpsHub.OpsGroup(lotId)).SendAsync("OpsAlert", new { lotId, code, message, sessionId });

    private Task ToUser(string userId, string method, object payload) =>
        string.IsNullOrEmpty(userId) ? Task.CompletedTask : opsHub.Clients.User(userId).SendAsync(method, payload);
}
