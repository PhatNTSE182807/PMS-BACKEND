using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PMS.Application.Models.Notifications;

namespace PMS.Application.Services;

/// <summary>The signed-in person's notification bell (customers, and Staff for their lot's cases): list, unread count, mark as read.</summary>
public interface INotificationService
{
    /// <summary>The newest notifications (at most <paramref name="take"/>) and the unread count.</summary>
    Task<NotificationListModel> GetMineAsync(string userId, bool unreadOnly, int take);

    Task<int> GetUnreadCountAsync(string userId);

    Task<NotificationModel> MarkReadAsync(string userId, long notificationId);

    /// <summary>Marks every unread notification as read; returns how many changed.</summary>
    Task<int> MarkAllReadAsync(string userId);

    Task DeleteAsync(string userId, long notificationId);
}

/// <summary>
/// Hands customer-facing events to the background worker (Hangfire), which writes the notification and
/// pushes it in real time. Call it after SaveChanges: the job reads the saved records.
/// </summary>
public interface IUserNotificationPublisher
{
    /// <summary>Staff approved or rejected a vehicle registration.</summary>
    void VehicleReviewed(Guid verificationId);

    /// <summary>A vehicle's entry was confirmed (check-in time).</summary>
    void SessionCheckedIn(long sessionId);

    /// <summary>A vehicle left, or its session was closed (check-out time).</summary>
    void SessionCheckedOut(long sessionId);

    /// <summary>
    /// A lot's price for one vehicle type changed. Customers with an upcoming booking for that type there get
    /// <paramref name="title"/> / <paramref name="message"/> (check your booking); every other active customer gets
    /// <paramref name="allTitle"/> / <paramref name="allMessage"/> (the new price).
    /// </summary>
    void LotPriceChanged(string title, string message, string eventKey, int lotId, int vehicleTypeId, string lotName,
        string allTitle = null, string allMessage = null);

    /// <summary>
    /// Something happened to a booked bay a walk-in took (BookedBayCase): <paramref name="notificationType"/> is one of the
    /// NotificationTypes BookedBayTaken, BookedBayReassigned, BookedBayFreed, BookingCancelledByLot, DepositRefunded or
    /// CompensationPaid. Tells the booking's customer, the lot's Staff and, once the case is handled, the lot's Manager.
    /// </summary>
    void BookedBayCaseChanged(long caseId, string notificationType);

    /// <summary>
    /// A booking notice: <paramref name="notificationType"/> is DepartureSoon or DepartureOverdue (a parked vehicle's
    /// scheduled departure; the overdue notice also goes to the lot's Staff), or BookingBayMoved (the booking left
    /// <paramref name="previousSlotId"/> because that bay was out of service).
    /// </summary>
    void ReservationNotice(long reservationId, string notificationType, int? previousSlotId = null);

    /// <summary>A Staff closed their cash drawer: tells the lot's Manager when, and how the count compares.</summary>
    void CashDrawerClosed(long drawerId);
}

/// <summary>Who hears about something that concerns every customer.</summary>
public interface ICustomerAudience
{
    /// <summary>The user ids of all active customer accounts.</summary>
    Task<IReadOnlyList<string>> ActiveCustomerIdsAsync();
}

/// <summary>Real-time push of a new notification to its owner (SignalR in PMS.API).</summary>
public interface IUserNotificationNotifier
{
    Task NotificationCreatedAsync(string userId, NotificationModel notification, int unreadCount);
}
