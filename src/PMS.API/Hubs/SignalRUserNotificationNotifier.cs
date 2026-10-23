using Microsoft.AspNetCore.SignalR;
using PMS.Application.Models.Notifications;
using PMS.Application.Services;

namespace PMS.API.Hubs;

/// <summary>
/// Pushes a new notification to its owner through the ParkingOpsHub (the customer is already
/// connected to it for reservations, payments and gate requests). Event name: "NotificationCreated".
/// </summary>
public class SignalRUserNotificationNotifier(IHubContext<ParkingOpsHub> opsHub) : IUserNotificationNotifier
{
    public Task NotificationCreatedAsync(string userId, NotificationModel notification, int unreadCount) =>
        string.IsNullOrEmpty(userId)
            ? Task.CompletedTask
            : opsHub.Clients.User(userId).SendAsync("NotificationCreated", new { notification, unreadCount });
}
