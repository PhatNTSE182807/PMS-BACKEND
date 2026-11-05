using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Notifications;
using PMS.Application.Models.Operations;
using PMS.Application.Models.VehicleDocuments;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Jobs;

/// <summary>
/// Customer notifications, run by Hangfire (see PMS.API): fire-and-forget jobs enqueued by
/// <see cref="IUserNotificationPublisher"/> once a registration is decided, a vehicle checks in / out or a lot's
/// price changes, and a daily clean-up of old read notifications. Each job reads the saved record, writes one
/// notification (never twice for the same event, so Hangfire retries are safe) and pushes it live.
/// </summary>
public class NotificationJobs(
    PmsDbContext pmsContext,
    TimeProvider clock,
    IUserNotificationNotifier notifier = null,
    ILogger<NotificationJobs> logger = null)
{
    public const string PurgeJobId = "purge-user-notifications";

    /// <summary>Read notifications older than this are deleted by the daily job.</summary>
    public const int ReadRetentionDays = 60;

    /// <summary>Unread ones are kept longer, but not forever.</summary>
    public const int UnreadRetentionDays = 180;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Staff approved or rejected a vehicle registration.</summary>
    public async Task<bool> VehicleReviewedAsync(Guid verificationId)
    {
        var verification = await pmsContext.VehicleVerifications.AsNoTracking()
            .Include(v => v.Vehicle)
            .FirstOrDefaultAsync(v => v.Id == verificationId);
        if (verification == null || string.IsNullOrEmpty(verification.UserId))
            return false;

        var approved = VehicleVerificationStatus.Verified.Contains(verification.Status);
        if (!approved && verification.Status != VehicleVerificationStatus.Rejected)
            return false;

        var plate = verification.Vehicle?.PlateNumber ?? "your vehicle";
        var vehicleName = string.Join(' ', new[] { verification.Vehicle?.Brand, verification.Vehicle?.Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var data = new Dictionary<string, string>
        {
            ["plateNumber"] = verification.Vehicle?.PlateNumber,
            ["vehicleId"] = verification.VehicleId.ToString(),
            ["vehicleName"] = vehicleName,
            ["note"] = verification.ReviewNote
        };

        var label = string.IsNullOrEmpty(vehicleName) ? plate : $"{plate} ({vehicleName})";
        var notification = approved
            ? new UserNotification
            {
                Type = NotificationTypes.VehicleApproved,
                Title = "Vehicle registration approved",
                Message = $"Staff approved the papers of {label}. You can now book bays and it is recognised at the gates."
            }
            : new UserNotification
            {
                Type = NotificationTypes.VehicleRejected,
                Title = "Vehicle registration rejected",
                Message = $"Staff rejected the papers of {label}" +
                          (string.IsNullOrWhiteSpace(verification.ReviewNote) ? "." : $": \"{verification.ReviewNote.Trim()}\".") +
                          " The vehicle was removed from your account; register it again with new photos."
            };

        notification.UserId = verification.UserId;
        notification.RefType = "VehicleVerification";
        notification.RefId = verification.Id.ToString();
        notification.EventAt = verification.DecidedAt ?? Now;
        return await SaveAndPushAsync(notification, data);
    }

    /// <summary>The vehicle's entry was confirmed: tells the owner when and where it checked in.</summary>
    public async Task<bool> SessionCheckedInAsync(long sessionId)
    {
        var (session, userId, lot) = await LoadSessionAsync(sessionId);
        if (session == null || userId == null || !SessionStatus.Inside.Contains(session.Status) && session.Status != SessionStatus.Completed)
            return false;

        var at = session.EntryTime;
        var data = SessionData(session, lot);
        return await SaveAndPushAsync(new UserNotification
        {
            UserId = userId,
            Type = NotificationTypes.CheckIn,
            Title = $"Checked in at {lot?.Name ?? "the parking lot"}",
            Message = $"Vehicle {session.EntryPlateNumber} checked in at {LocalTime(at, lot)}. Ticket {session.SessionCode}.",
            RefType = "ParkingSession",
            RefId = session.Id.ToString(),
            EventAt = at
        }, data);
    }

    /// <summary>The vehicle left (or a Manager closed the session): check-out time, duration and fee.</summary>
    public async Task<bool> SessionCheckedOutAsync(long sessionId)
    {
        var (session, userId, lot) = await LoadSessionAsync(sessionId);
        if (session == null || userId == null || session.ExitTime == null)
            return false;

        var at = session.ExitTime.Value;
        var minutes = session.TotalDurationMinutes ?? (int)Math.Max(0, Math.Round((at - session.EntryTime).TotalMinutes));
        var fee = session.FinalFee > 0 ? session.FinalFee : session.PaidAmount;
        var data = SessionData(session, lot);
        data["exitTime"] = Iso(at);
        data["durationMinutes"] = minutes.ToString(CultureInfo.InvariantCulture);
        data["fee"] = fee.ToString("0.##", CultureInfo.InvariantCulture);

        var closedByManager = session.Status == SessionStatus.AbnormalClosed;
        if (closedByManager)
            data["note"] = session.ClosedNote;

        var message = $"Vehicle {session.EntryPlateNumber} checked out at {LocalTime(at, lot)} after {Duration(minutes)}"
                      + (fee > 0 ? $". Fee: {fee.ToString("N0", CultureInfo.InvariantCulture)} VND." : ".")
                      + (closedByManager ? $" The session was closed by the lot Manager{(string.IsNullOrWhiteSpace(session.ClosedNote) ? "" : $": {session.ClosedNote}")}." : "");

        return await SaveAndPushAsync(new UserNotification
        {
            UserId = userId,
            Type = NotificationTypes.CheckOut,
            Title = $"Checked out of {lot?.Name ?? "the parking lot"}",
            Message = message,
            RefType = "ParkingSession",
            RefId = session.Id.ToString(),
            EventAt = at
        }, data);
    }

    /// <summary>
    /// A lot's price for one vehicle type changed: one notification per customer with an upcoming booking for that
    /// type there. A checked-in booking is left out, for its vehicle keeps the price it entered with.
    /// A Hangfire retry only adds the customers it missed. Returns the number of notifications written.
    /// </summary>
    public async Task<int> LotPriceChangedAsync(string title, string message, string eventKey, int lotId, int vehicleTypeId, string lotName)
    {
        var told = await pmsContext.UserNotifications
            .Where(n => n.Type == NotificationTypes.PriceChanged && n.RefId == eventKey)
            .Select(n => n.UserId)
            .ToListAsync();
        // No arrival-time filter: a late customer who has not entered yet still pays the price in force at entry
        var booked = await pmsContext.Reservations.AsNoTracking()
            .Where(r => r.ParkingLotId == lotId
                        && ReservationStatus.Upcoming.Contains(r.Status)
                        && r.UserId != null
                        // Older bookings have no type of their own: their vehicle's type decides
                        && (r.VehicleTypeId == vehicleTypeId
                            || (r.VehicleTypeId == null && pmsContext.Vehicles.Any(v => v.Id == r.VehicleId && v.VehicleTypeId == vehicleTypeId))))
            .Select(r => r.UserId)
            .Distinct()
            .ToListAsync();
        var recipients = booked.Except(told).ToList();

        var dataJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["lotId"] = lotId.ToString(CultureInfo.InvariantCulture),
            ["lotName"] = lotName
        });

        var notifications = recipients.Select(userId => new UserNotification
        {
            UserId = userId,
            Type = NotificationTypes.PriceChanged,
            Title = title,
            Message = message,
            RefType = "LotPriceChange",
            RefId = eventKey,
            EventAt = Now,
            DataJson = dataJson,
            CreatedAt = Now
        }).ToList();
        pmsContext.UserNotifications.AddRange(notifications);
        await pmsContext.SaveChangesAsync();

        if (notifier != null && notifications.Count > 0)
        {
            var unread = await pmsContext.UserNotifications
                .Where(n => n.ReadAt == null && recipients.Contains(n.UserId))
                .GroupBy(n => n.UserId)
                .Select(g => new { UserId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.UserId, g => g.Count);

            foreach (var notification in notifications)
            {
                try
                {
                    await notifier.NotificationCreatedAsync(notification.UserId, NotificationService.ToModel(notification), unread.GetValueOrDefault(notification.UserId));
                }
                catch (Exception ex)
                {
                    // Already saved, so the customer sees it on the next read; one dead connection must not stop the others
                    logger?.LogWarning(ex, "Could not push price notification {NotificationId} to its owner", notification.Id);
                }
            }
        }

        return notifications.Count;
    }

    /// <summary>Daily: drops read notifications after 60 days and unread ones after 180. Returns the number deleted.</summary>
    public async Task<int> PurgeOldNotificationsAsync()
    {
        var readCutoff = Now.AddDays(-ReadRetentionDays);
        var unreadCutoff = Now.AddDays(-UnreadRetentionDays);
        var old = await pmsContext.UserNotifications
            .Where(n => (n.ReadAt != null && n.CreatedAt <= readCutoff) || n.CreatedAt <= unreadCutoff)
            .ToListAsync();
        pmsContext.UserNotifications.RemoveRange(old);
        await pmsContext.SaveChangesAsync();
        return old.Count;
    }

    private async Task<(ParkingSession Session, string UserId, ParkingLot Lot)> LoadSessionAsync(long sessionId)
    {
        var session = await pmsContext.ParkingSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session?.CustomerId == null)
            return (session, null, null);

        var userId = await pmsContext.Customers.Where(c => c.Id == session.CustomerId).Select(c => c.UserId).FirstOrDefaultAsync();
        var lot = await pmsContext.ParkingLots.AsNoTracking().FirstOrDefaultAsync(l => l.Id == session.ParkingLotId);
        return (session, string.IsNullOrEmpty(userId) ? null : userId, lot);
    }

    private static Dictionary<string, string> SessionData(ParkingSession session, ParkingLot lot) => new()
    {
        ["plateNumber"] = session.EntryPlateNumber,
        ["sessionCode"] = session.SessionCode,
        ["lotId"] = session.ParkingLotId.ToString(CultureInfo.InvariantCulture),
        ["lotName"] = lot?.Name,
        ["entryTime"] = Iso(session.EntryTime)
    };

    /// <summary>Writes the notification unless this event was already notified, then pushes it to the owner.</summary>
    private async Task<bool> SaveAndPushAsync(UserNotification notification, Dictionary<string, string> data)
    {
        // Hangfire retries a failed job: one event, one notification
        if (await pmsContext.UserNotifications.AnyAsync(n => n.Type == notification.Type && n.RefId == notification.RefId && n.UserId == notification.UserId))
            return false;

        notification.DataJson = JsonSerializer.Serialize(data.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value));
        notification.CreatedAt = Now;
        pmsContext.UserNotifications.Add(notification);
        await pmsContext.SaveChangesAsync();

        if (notifier != null)
        {
            var unread = await pmsContext.UserNotifications.CountAsync(n => n.UserId == notification.UserId && n.ReadAt == null);
            await notifier.NotificationCreatedAsync(notification.UserId, NotificationService.ToModel(notification), unread);
        }

        return true;
    }

    /// <summary>"10:32, 23/10/2026" in the lot's own time zone (Vietnam by default).</summary>
    private static string LocalTime(DateTime utc, ParkingLot lot)
    {
        // Falls back to Vietnam time (UTC+7) when the lot's zone is unknown on this server
        var zone = ParkingText.FindTimeZone(lot?.TimeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        return local.ToString("HH:mm, dd/MM/yyyy", CultureInfo.InvariantCulture);
    }

    private static string Duration(int minutes) =>
        minutes < 60 ? $"{minutes} min"
        : minutes % 60 == 0 ? $"{minutes / 60} h"
        : $"{minutes / 60} h {minutes % 60} min";

    private static string Iso(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
}
