using PMS.Application.Services.Impl;
namespace PMS.Application.Jobs;
public class WorkRequestJobs(WorkSchedulingService schedules)
{
    public Task<int> RunAsync() => schedules.ProcessWorkRequestDeadlinesAsync();
}
