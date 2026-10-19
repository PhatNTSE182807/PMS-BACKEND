using Microsoft.AspNetCore.SignalR;
using PMS.Application.Services;

namespace PMS.API.Hubs;

/// <summary>Sends check status changes to the uploader and to the Staff review group. Ids and status only, no personal data.</summary>
public class SignalRVehicleVerificationNotifier(IHubContext<VehicleVerificationHub> hub) : IVehicleVerificationNotifier
{
    public async Task VerificationChangedAsync(string userId, Guid verificationId, Guid vehicleId, string status)
    {
        var payload = new { verificationId, vehicleId, status };
        await hub.Clients.User(userId).SendAsync("VerificationChanged", payload);
        await hub.Clients.Group(VehicleVerificationHub.ReviewersGroup).SendAsync("VerificationChanged", payload);
    }
}
