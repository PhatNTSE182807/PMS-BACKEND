using Microsoft.AspNetCore.SignalR;
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
}
