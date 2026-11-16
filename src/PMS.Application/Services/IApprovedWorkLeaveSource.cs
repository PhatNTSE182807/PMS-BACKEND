namespace PMS.Application.Services;

public record ApprovedWorkLeave(int EmployeeId, DateTime StartsAt, DateTime EndsAt);
public record ApprovedWorkLeaveResult(bool Available, IReadOnlyList<ApprovedWorkLeave> Leaves);
/// <summary>Read-only integration contract. Never infer approval from an empty calendar cell.</summary>
public interface IApprovedWorkLeaveSource
{
    Task<ApprovedWorkLeaveResult> ReadAsync(int lotId, DateTime from, DateTime to);
}
public sealed class UnavailableWorkLeaveSource : IApprovedWorkLeaveSource
{
    public Task<ApprovedWorkLeaveResult> ReadAsync(int lotId, DateTime from, DateTime to) =>
        Task.FromResult(new ApprovedWorkLeaveResult(false, []));
}
public sealed class ApprovedWorkLeaveSource(PMS.DataAccess.Persistence.PmsDbContext db) : IApprovedWorkLeaveSource
{
    public async Task<ApprovedWorkLeaveResult> ReadAsync(int lotId, DateTime from, DateTime to)
    {
        var rows = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.WorkLeaveRequests.Where(l => l.ParkingLotId == lotId && l.Status == "Approved" && l.StartsAt < to && from < l.EndsAt)
                .Select(l => new ApprovedWorkLeave(l.EmployeeId, l.StartsAt, l.EndsAt)));
        return new ApprovedWorkLeaveResult(true, rows);
    }
}
