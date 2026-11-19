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

    public Task<int> RunAttendanceAsync() => duty.UpdateDueAttendanceAsync();

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
