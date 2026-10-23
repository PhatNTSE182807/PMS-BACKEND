using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.Notifications;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <inheritdoc />
public class NotificationService(PmsDbContext pmsContext, TimeProvider clock) : INotificationService
{
    /// <summary>The bell never loads more than this many at once.</summary>
    public const int MaxTake = 100;

    public async Task<NotificationListModel> GetMineAsync(string userId, bool unreadOnly, int take)
    {
        take = Math.Clamp(take <= 0 ? 30 : take, 1, MaxTake);
        var query = pmsContext.UserNotifications.AsNoTracking().Where(n => n.UserId == userId);
        if (unreadOnly)
            query = query.Where(n => n.ReadAt == null);

        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Take(take)
            .ToListAsync();

        return new NotificationListModel
        {
            Items = items.Select(ToModel).ToList(),
            UnreadCount = await GetUnreadCountAsync(userId)
        };
    }

    public Task<int> GetUnreadCountAsync(string userId) =>
        pmsContext.UserNotifications.CountAsync(n => n.UserId == userId && n.ReadAt == null);

    public async Task<NotificationModel> MarkReadAsync(string userId, long notificationId)
    {
        var notification = await FindOwnAsync(userId, notificationId);
        if (notification.ReadAt == null)
        {
            notification.ReadAt = clock.GetUtcNow().UtcDateTime;
            await pmsContext.SaveChangesAsync();
        }

        return ToModel(notification);
    }

    public async Task<int> MarkAllReadAsync(string userId)
    {
        var unread = await pmsContext.UserNotifications
            .Where(n => n.UserId == userId && n.ReadAt == null)
            .ToListAsync();
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var notification in unread)
            notification.ReadAt = now;
        await pmsContext.SaveChangesAsync();
        return unread.Count;
    }

    public async Task DeleteAsync(string userId, long notificationId)
    {
        var notification = await FindOwnAsync(userId, notificationId);
        pmsContext.UserNotifications.Remove(notification);
        await pmsContext.SaveChangesAsync();
    }

    private async Task<UserNotification> FindOwnAsync(string userId, long notificationId) =>
        // Someone else's notification looks exactly like a missing one
        await pmsContext.UserNotifications.FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId)
        ?? throw new NotFoundException("Notification not found.");

    internal static NotificationModel ToModel(UserNotification n) => new()
    {
        Id = n.Id,
        Type = n.Type,
        Title = n.Title,
        Message = n.Message,
        RefType = n.RefType,
        RefId = n.RefId,
        EventAt = n.EventAt is { } e ? Utc(e) : null,
        Data = ReadData(n.DataJson),
        CreatedAt = Utc(n.CreatedAt),
        ReadAt = n.ReadAt is { } r ? Utc(r) : null
    };

    private static Dictionary<string, string> ReadData(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
