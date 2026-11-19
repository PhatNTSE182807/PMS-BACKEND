using Microsoft.EntityFrameworkCore;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence;

namespace PMS.Application.Jobs;

/// <summary>Duty board jobs: absences and forgotten check-outs every minute, payroll preparation on the 1st,
/// and pushing committed notices plus the lot's refresh signal.</summary>
public class DutyJobs(DutyScheduleService duty, PmsDbContext db, IUserNotificationNotifier notifier, IParkingRealtimeNotifier realtime)
{
    public const string AttendanceJobId = "duty-attendance";
    public const string PayrollJobId = "duty-payroll-ready";

    /// <summary>When this API process last brought the attendance up to date (UTC); null before the first run.</summary>
    private static DateTime? _attendanceCheckedAt;

    /// <summary>
    /// Shifts start and end on whole hours of the lot's local time, which always fall on a quarter hour in UTC
    /// (every time zone is offset by a multiple of 15 minutes). Nothing falls due between two quarter hours, so the
    /// minutely job only reads the duty boards on its first run after each one: 4 runs an hour instead of 60.
    /// A board change for a shift that already ended is still picked up within 15 minutes.
    /// </summary>
    public async Task<int> RunAttendanceAsync()
    {
        var now = DateTime.UtcNow;
        static long Quarter(DateTime utc) => utc.Ticks / TimeSpan.FromMinutes(15).Ticks;
        if (_attendanceCheckedAt is DateTime last && Quarter(now) == Quarter(last) && now >= last)
            return 0;

        var changes = await duty.UpdateDueAttendanceAsync();
        // Only after a full pass: a run that failed half way is tried again the next minute
        _attendanceCheckedAt = now;
        return changes;
    }

    public Task<int> PreparePayrollAsync() => duty.PreparePayrollAsync();

    public async Task PublishAsync(int lotId, long[] notificationIds)
    {
        var notices = await db.UserNotifications.AsNoTracking().Where(n => notificationIds.Contains(n.Id)).ToListAsync();
        foreach (var n in notices)
        {
            var unread = await db.UserNotifications.CountAsync(x => x.UserId == n.UserId && x.ReadAt == null);
            await notifier.NotificationCreatedAsync(n.UserId, NotificationService.ToModel(n), unread);
        }
        // Refresh signal only: no names or times go through the lot channel
        if (lotId > 0) await realtime.DutyScheduleChangedAsync(lotId);
    }
}
