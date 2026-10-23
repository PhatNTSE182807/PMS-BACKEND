using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using PMS.Application.Services.Impl;

namespace PMS.API.Hubs;

/// <summary>
/// Private real-time channel of reservations, gate sessions and payments (SRS FR-3D-09 internal channel).
/// Customers receive "ReservationChanged", "ReservationReminder", "PaymentChanged" and "GateRequestChanged" for their own
/// bookings (SignalR user = JWT NameIdentifier). Staff and Managers call JoinLot for each lot in their
/// scope and receive "SessionChanged", "PaymentChanged", "ZoneCounterChanged", "GateRequestChanged" and "OpsAlert".
/// Mapped at /api/hubs/parking-ops; the browser passes the JWT as access_token.
/// </summary>
[Authorize]
public class ParkingOpsHub(LotScopeService lotScope) : Hub
{
    public const string Path = "/api/hubs/parking-ops";

    public static string OpsGroup(int lotId) => $"ops-lot-{lotId}";

    /// <summary>Joins the lot's internal channel; refused outside the caller's lot scope.</summary>
    public async Task JoinLot(int lotId)
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var role = Context.User?.FindFirstValue(ClaimTypes.Role);
        if (!await lotScope.CanOperateAsync(userId, role, lotId))
            throw new HubException("You are not assigned to this parking lot.");

        await Groups.AddToGroupAsync(Context.ConnectionId, OpsGroup(lotId));
    }

    public Task LeaveLot(int lotId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, OpsGroup(lotId));
}
