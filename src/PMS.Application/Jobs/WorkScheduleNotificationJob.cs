using Microsoft.EntityFrameworkCore;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence;

namespace PMS.Application.Jobs;

/// <summary>The notification is saved with the roster; Hangfire only pushes the committed message.</summary>
public class WorkScheduleNotificationJob(PmsDbContext db, IUserNotificationNotifier notifier)
{
    public async Task PushAsync(long[] notificationIds)
    {
        var notifications = await db.UserNotifications.AsNoTracking().Where(n => notificationIds.Contains(n.Id)).ToListAsync();
        foreach (var n in notifications)
        {
            var unread = await db.UserNotifications.CountAsync(x => x.UserId == n.UserId && x.ReadAt == null);
            await notifier.NotificationCreatedAsync(n.UserId, NotificationService.ToModel(n), unread);
        }
    }
}
