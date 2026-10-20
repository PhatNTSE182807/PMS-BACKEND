using Microsoft.EntityFrameworkCore;
using PMS.Application.Models.Operations;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Jobs;

/// <summary>What one run of the timed parking work changed (for logs and tests).</summary>
public sealed record ParkingJobRun(int ExpiredReservations, int NoShows, int Reminders, int CancelledEntries, int ReleasedPaymentRequests, int CheckedPayments, int RetriedRefunds);

/// <summary>
/// Timed parking work run by Hangfire (registered in PMS.API): deposit deadlines (BR-06), no-shows (BR-05),
/// reminders (FR-RSV-10), vehicles that turned around at the barrier, abandoned payment requests,
/// pending gateway payments (BR-11, FR-PAY-08), refund retries (FR-PAY-10) and overdue vehicles (BR-20).
/// </summary>
public class ParkingJobs(
    PmsDbContext pmsContext,
    ParkingOptions options,
    ReservationService reservations,
    GateService gates,
    IPaymentService payments,
    ParkingInventory inventory,
    TimeProvider clock,
    IParkingRealtimeNotifier notifier = null)
{
    public const string MinutelyJobId = "parking-timeouts";
    public const string OverdueJobId = "parking-overdue-sessions";

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Everything that must happen within a minute of its deadline.</summary>
    public async Task<ParkingJobRun> RunMinutelyAsync()
    {
        var expired = await ExpireUnpaidReservationsAsync();
        var noShows = await MarkNoShowsAsync();
        var reminders = await SendRemindersAsync();
        var cancelled = await CancelUnconfirmedEntriesAsync();
        var released = await ReleaseStalePaymentRequestsAsync();
        var checkedPayments = await payments.CheckPendingPaymentsAsync();
        var refunds = await payments.RetryPendingRefundsAsync();
        return new ParkingJobRun(expired, noShows, reminders, cancelled, released, checkedPayments, refunds);
    }

    /// <summary>BR-06 / 4.2.5: the deposit was not paid in time → Expired, slot back to Available.</summary>
    public async Task<int> ExpireUnpaidReservationsAsync()
    {
        var due = await pmsContext.Reservations
            .Where(r => r.Status == ReservationStatus.PendingDeposit && r.DepositDueAt != null && r.DepositDueAt <= Now)
            .ToListAsync();
        foreach (var reservation in due)
        {
            await reservations.EndAsync(reservation, ReservationStatus.Expired, "Quá hạn thanh toán cọc", "System", null);
            QueueReservation(reservation);
        }

        await SaveAndFlushAsync();
        return due.Count;
    }

    /// <summary>FR-RSV-05 / BR-05: not checked in before the hold ended → No-show, slot free, deposit kept.</summary>
    public async Task<int> MarkNoShowsAsync()
    {
        var due = await pmsContext.Reservations
            .Where(r => r.Status == ReservationStatus.Confirmed && r.EndTime <= Now)
            .ToListAsync();
        foreach (var reservation in due)
        {
            // A vehicle that already passed the entry lane is not a no-show
            if (await pmsContext.ParkingSessions.AnyAsync(s => s.ReservationId == reservation.Id && s.Status == SessionStatus.PendingEntry))
                continue;
            await reservations.EndAsync(reservation, ReservationStatus.NoShow, "Không check-in trong thời gian giữ chỗ", "System", null);
            QueueReservation(reservation);
        }

        await SaveAndFlushAsync();
        return due.Count;
    }

    /// <summary>FR-RSV-10: before the arrival time and before the hold ends.</summary>
    public async Task<int> SendRemindersAsync()
    {
        var rules = options.Reservations;
        var arrivalSoon = Now.AddMinutes(rules.ReminderBeforeMinutes);
        var holdEndingSoon = Now.AddMinutes(rules.HoldEndReminderMinutes);
        var confirmed = await pmsContext.Reservations
            .Where(r => r.Status == ReservationStatus.Confirmed
                        && ((r.ReminderSentAt == null && r.StartTime <= arrivalSoon)
                            || (r.HoldReminderSentAt == null && r.EndTime <= holdEndingSoon)))
            .ToListAsync();

        var sent = 0;
        foreach (var reservation in confirmed)
        {
            var (userId, id) = (reservation.UserId, reservation.Id);
            if (reservation.ReminderSentAt == null && reservation.StartTime <= arrivalSoon)
            {
                reservation.ReminderSentAt = Now;
                var at = reservation.StartTime;
                inventory.Queue(() => notifier?.ReservationReminderAsync(userId, id, "Arrival", at) ?? Task.CompletedTask);
                sent++;
            }
            if (reservation.HoldReminderSentAt == null && reservation.EndTime <= holdEndingSoon && reservation.StartTime <= Now)
            {
                reservation.HoldReminderSentAt = Now;
                var at = reservation.EndTime;
                inventory.Queue(() => notifier?.ReservationReminderAsync(userId, id, "HoldEnding", at) ?? Task.CompletedTask);
                sent++;
            }
        }

        await SaveAndFlushAsync();
        return sent;
    }

    /// <summary>4.2.1: the barrier opened but no sensor signal came → the session is cancelled, counters unchanged.</summary>
    public async Task<int> CancelUnconfirmedEntriesAsync()
    {
        var cutoff = Now.AddMinutes(-options.Gate.EntryConfirmTimeoutMinutes);
        var stale = await pmsContext.ParkingSessions
            .Where(s => s.Status == SessionStatus.PendingEntry && s.EntryTime <= cutoff)
            .ToListAsync();
        foreach (var session in stale)
            await gates.CancelPendingEntryAsync(session, "Không có tín hiệu cảm biến: xe quay đầu");

        await SaveAndFlushAsync();
        return stale.Count;
    }

    /// <summary>4.3.2 "hủy / quá thời gian chờ": a fee request nobody paid goes back to parked.</summary>
    public async Task<int> ReleaseStalePaymentRequestsAsync()
    {
        var cutoff = Now.AddMinutes(-options.Gate.PaymentLinkMinutes);
        var stale = await pmsContext.ParkingSessions
            .Where(s => s.Status == SessionStatus.PendingPayment && s.UpdatedAt != null && s.UpdatedAt <= cutoff)
            .ToListAsync();

        var released = 0;
        foreach (var session in stale)
        {
            if (await pmsContext.Payments.AnyAsync(p => p.SessionId == session.Id && p.Status == PaymentStatus.Pending && p.ExpiresAt > Now))
                continue;
            session.Status = SessionStatus.Active;
            session.UpdatedAt = Now;
            var (lotId, id, plate) = (session.ParkingLotId, session.Id, session.EntryPlateNumber);
            inventory.Queue(() => notifier?.SessionChangedAsync(lotId, id, SessionStatus.Active, plate) ?? Task.CompletedTask);
            released++;
        }

        await SaveAndFlushAsync();
        return released;
    }

    /// <summary>
    /// FR-OPS-16 / BR-20: alert the lot (and the owner) once a vehicle is overdue; past the abandoned
    /// threshold open an incident (SessionViolation "Abandoned").
    /// </summary>
    public async Task<int> FlagOverdueSessionsAsync()
    {
        var overdueBefore = Now.AddHours(-options.Gate.OverdueHours);
        var abandonedBefore = Now.AddDays(-options.Gate.AbandonedDays);
        var sessions = await pmsContext.ParkingSessions
            .Where(s => SessionStatus.Inside.Contains(s.Status) && s.EntryTime <= overdueBefore
                        && (s.OverdueNotifiedAt == null || (s.AbandonedFlaggedAt == null && s.EntryTime <= abandonedBefore)))
            .ToListAsync();

        foreach (var session in sessions)
        {
            var (lotId, id, code) = (session.ParkingLotId, session.Id, session.SessionCode);
            if (session.OverdueNotifiedAt == null)
            {
                session.OverdueNotifiedAt = Now;
                inventory.Queue(() => notifier?.OpsAlertAsync(lotId, "SESSION_OVERDUE", $"Xe {code} đỗ quá {options.Gate.OverdueHours} giờ.", id) ?? Task.CompletedTask);
            }

            if (session.AbandonedFlaggedAt == null && session.EntryTime <= abandonedBefore)
            {
                session.AbandonedFlaggedAt = Now;
                pmsContext.SessionViolations.Add(new SessionViolation
                {
                    SessionId = session.Id,
                    ViolationType = "Abandoned",
                    Note = $"Xe bỏ quên quá {options.Gate.AbandonedDays} ngày",
                    ReportedBy = "System",
                    ReportedAt = Now
                });
                inventory.Queue(() => notifier?.OpsAlertAsync(lotId, "SESSION_ABANDONED", $"Xe {code} có thể bị bỏ quên: đã tạo sự cố.", id) ?? Task.CompletedTask);
            }
        }

        await SaveAndFlushAsync();
        return sessions.Count;
    }

    private void QueueReservation(Reservation r)
    {
        var (userId, id, lotId, status, slotId) = (r.UserId, r.Id, r.ParkingLotId, r.Status, r.SlotId);
        inventory.Queue(() => notifier?.ReservationChangedAsync(userId, id, lotId, status, slotId) ?? Task.CompletedTask);
    }

    private async Task SaveAndFlushAsync()
    {
        await pmsContext.SaveChangesAsync();
        await inventory.FlushAsync();
    }
}
