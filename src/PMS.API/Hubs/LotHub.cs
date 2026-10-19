using Microsoft.AspNetCore.SignalR;

namespace PMS.API.Hubs;

/// <summary>
/// Public real-time channel of a lot's 3D model (SRS FR-3D-03, FR-3D-08): slot and lot statuses,
/// never plates. Clients join one group per lot they show. Mapped at /api/hubs/lots.
/// </summary>
public class LotHub : Hub
{
    public const string Path = "/api/hubs/lots";

    public static string Group(int lotId) => $"lot-{lotId}";

    public Task JoinLot(int lotId) => Groups.AddToGroupAsync(Context.ConnectionId, Group(lotId));

    public Task LeaveLot(int lotId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(lotId));
}
