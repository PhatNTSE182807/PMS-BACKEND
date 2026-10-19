using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace PMS.API.Hubs;

/// <summary>
/// Private channel for vehicle paper checks: the uploader receives "VerificationChanged" for their own
/// checks (SignalR user = JWT NameIdentifier); Staff, Managers and Admins also join the review group to
/// see the queue change. Mapped at /api/hubs/vehicle-verifications; the browser passes the JWT as access_token.
/// </summary>
[Authorize]
public class VehicleVerificationHub : Hub
{
    public const string Path = "/api/hubs/vehicle-verifications";

    public const string ReviewersGroup = "vehicle-verification-reviewers";

    public override async Task OnConnectedAsync()
    {
        var user = Context.User;
        if (user != null && (user.IsInRole("Staff") || user.IsInRole("Manager") || user.IsInRole("Admin")))
            await Groups.AddToGroupAsync(Context.ConnectionId, ReviewersGroup);

        await base.OnConnectedAsync();
    }
}
