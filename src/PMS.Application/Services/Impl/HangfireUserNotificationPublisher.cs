using System;
using Hangfire;
using PMS.Application.Jobs;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Enqueues the notification work on Hangfire so a slow database or SignalR push never delays the
/// Staff action, and a failure is retried by Hangfire instead of being lost.
/// </summary>
public class HangfireUserNotificationPublisher(IBackgroundJobClient jobs) : IUserNotificationPublisher
{
    public void VehicleReviewed(Guid verificationId) =>
        jobs.Enqueue<NotificationJobs>(j => j.VehicleReviewedAsync(verificationId));

    public void SessionCheckedIn(long sessionId) =>
        jobs.Enqueue<NotificationJobs>(j => j.SessionCheckedInAsync(sessionId));

    public void SessionCheckedOut(long sessionId) =>
        jobs.Enqueue<NotificationJobs>(j => j.SessionCheckedOutAsync(sessionId));

    public void LotPriceChanged(string title, string message, string eventKey, int lotId, int vehicleTypeId, string lotName) =>
        jobs.Enqueue<NotificationJobs>(j => j.LotPriceChangedAsync(title, message, eventKey, lotId, vehicleTypeId, lotName));

    public void BookedBayCaseChanged(long caseId, string notificationType) =>
        jobs.Enqueue<NotificationJobs>(j => j.BookedBayCaseAsync(caseId, notificationType));

    public void ReservationNotice(long reservationId, string notificationType, int? previousSlotId = null) =>
        jobs.Enqueue<NotificationJobs>(j => j.ReservationNoticeAsync(reservationId, notificationType, previousSlotId));

    public void CashDrawerClosed(long drawerId) =>
        jobs.Enqueue<NotificationJobs>(j => j.CashDrawerClosedAsync(drawerId));
}
