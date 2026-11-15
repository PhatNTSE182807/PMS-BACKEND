using PMS.Application.Services;
using PMS.Application.Services.Impl;

namespace PMS.Application.Jobs;

public class WorkAttendanceJobs(WorkSchedulingService schedules, WorkScheduleNotificationJob notifications, IParkingRealtimeNotifier notifier)
{
    public Task<int> RunAsync() => schedules.UpdateDueAttendanceAsync();

    public async Task PublishAsync(int lotId, long[] notificationIds)
    {
        await notifications.PushAsync(notificationIds);
        // Invalidation signal only. Other Staff never receive a colleague's actual times through the lot channel.
        await notifier.WorkAttendanceChangedAsync(lotId);
    }
}
