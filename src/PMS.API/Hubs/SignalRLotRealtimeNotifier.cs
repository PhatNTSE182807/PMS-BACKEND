using Microsoft.AspNetCore.SignalR;
using PMS.Application.Models.Layout;
using PMS.Application.Services;

namespace PMS.API.Hubs;

/// <summary>Sends lot changes to the clients in that lot's <see cref="LotHub"/> group.</summary>
public class SignalRLotRealtimeNotifier(IHubContext<LotHub> hub) : ILotRealtimeNotifier
{
    public Task SlotStatusChangedAsync(int lotId, int slotId, string status) =>
        hub.Clients.Group(LotHub.Group(lotId)).SendAsync("SlotStatusChanged", new { lotId, slotId, status });

    public Task LotStatusChangedAsync(int lotId, string status) =>
        hub.Clients.Group(LotHub.Group(lotId)).SendAsync("LotStatusChanged", new { lotId, status });

    public Task LayoutChangedAsync(int lotId) =>
        hub.Clients.Group(LotHub.Group(lotId)).SendAsync("LayoutChanged", new { lotId });

    public Task LotLiveChangedAsync(LotLiveChangesModel changes) =>
        hub.Clients.Group(LotHub.Group(changes.ParkingLotId)).SendAsync("LotLiveChanged", changes);

    public Task LotPriceChangedAsync(int lotId, string state, string message) =>
        hub.Clients.Group(LotHub.Group(lotId)).SendAsync("LotPriceChanged", new { lotId, state, message });
}
