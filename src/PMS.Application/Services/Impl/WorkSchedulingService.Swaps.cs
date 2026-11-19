using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.WorkScheduling;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;
public partial class WorkSchedulingService
{
    public async Task<WorkSwapRequest> CreateSwapAsync(CreateSwapInput input)
    {
        var initial = await RequestContextAsync(input.AssignmentId); await EnsureRequestOwnerAsync(initial);
        return await WorkflowAsync(initial.Roster.ParkingLotId, async notices => {
            var a = await RequestContextAsync(input.AssignmentId); await EnsureRequestOwnerAsync(a);
            var b = await RequestContextAsync(input.OtherAssignmentId);
            if (a.Roster.ParkingLotId != b.Roster.ParkingLotId || a.Employee.UserId == b.Employee.UserId) throw new BadRequestException("Choose another Staff member's Published assignment at this lot.");
            if (Now >= a.Shift.StartsAt || Now >= b.Shift.StartsAt) throw new BadRequestException("Both shifts must not have started.");
            await EnsureNoActualWorkAsync(a.Assignment.Id); await EnsureNoActualWorkAsync(b.Assignment.Id);
            await EnsureNoPendingRequestAsync(a.Assignment.Id); await EnsureNoPendingRequestAsync(b.Assignment.Id);
            await EnsureReplacementAsync(a.Roster.ParkingLotId, b.Employee.Id, a.Shift, [a.Assignment.Id, b.Assignment.Id]);
            await EnsureReplacementAsync(a.Roster.ParkingLotId, a.Employee.Id, b.Shift, [a.Assignment.Id, b.Assignment.Id]);
            var swap = new WorkSwapRequest { ParkingLotId = a.Roster.ParkingLotId, AssignmentId = a.Assignment.Id, OtherAssignmentId = b.Assignment.Id,
                EmployeeId = a.Employee.Id, OtherEmployeeId = b.Employee.Id, Reason = RequestReason(input.Reason), CreatedAt = Now };
            db.WorkSwapRequests.Add(swap); await SaveWorkChangesAsync();
            await WorkflowEventAsync(swap.ParkingLotId, "Swap", swap.Id, "Submitted", null, swap, swap.Reason, [a.Employee.UserId, b.Employee.UserId], notices);
            return swap;
        });
    }

    public async Task<WorkSwapRequest> DecideSwapAsync(int id, WorkRequestCommand input)
    {
        if (input.Reason?.Length > 1000) throw new BadRequestException("Reason must be at most 1000 characters.");
        var initial = await db.WorkSwapRequests.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id) ?? throw new NotFoundException("Swap request not found.");
        return await WorkflowAsync(initial.ParkingLotId, async notices => {
            var swap = await db.WorkSwapRequests.SingleAsync(s => s.Id == id); CheckRevision(swap.Revision, input.Revision);
            var a = await RequestContextAsync(swap.AssignmentId, false); var b = await RequestContextAsync(swap.OtherAssignmentId, false);
            var before = JsonSerializer.Serialize(swap); var reason = input.Reason;
            if (input.Action == "Cancel") {
                await EnsureRequestOwnerAsync(a);
                if (swap.Status is not ("AwaitingColleague" or "PendingManager")) throw new ConflictException("This swap is already final.");
                swap.Status = "Cancelled";
            }
            else if (input.Action is "Accept" or "Decline") {
                await EnsureRequestOwnerAsync(b);
                if (swap.Status != "AwaitingColleague") throw new ConflictException("This proposal no longer awaits colleague consent.");
                if (Now >= a.Shift.StartsAt || Now >= b.Shift.StartsAt) throw new BadRequestException("The swap response window has ended.");
                swap.Status = input.Action == "Accept" ? "PendingManager" : "Declined";
            }
            else {
                await EnsureManagerAsync(swap.ParkingLotId); reason = RequestReason(input.Reason);
                if (swap.Status != "PendingManager") throw new BadRequestException("Colleague consent is required before Manager approval/rejection.");
                if (input.Action == "Approve") {
                    a = await RequestContextAsync(swap.AssignmentId); b = await RequestContextAsync(swap.OtherAssignmentId);
                    if (Now >= a.Shift.StartsAt || Now >= b.Shift.StartsAt) throw new BadRequestException("Both shifts must not have started.");
                    await EnsureNoActualWorkAsync(a.Assignment.Id); await EnsureNoActualWorkAsync(b.Assignment.Id);
                    var excluded = new[] { a.Assignment.Id, b.Assignment.Id };
                    await EnsureReplacementAsync(swap.ParkingLotId, b.Employee.Id, a.Shift, excluded);
                    await EnsureReplacementAsync(swap.ParkingLotId, a.Employee.Id, b.Shift, excluded);
                    var snapshots = await RequestSnapshotsAsync([a.Roster.Id, b.Roster.Id]);
                    foreach (var row in new[] { a, b }) { row.Assignment.IsCancelled = true; row.Assignment.IsManuallyEdited = true; row.Assignment.CancellationKind = "Swapped"; }
                    db.DutyAssignments.AddRange(
                        new DutyAssignment { DutyRosterShiftId = a.Shift.Id, EmployeeId = b.Employee.Id, Source = "Swap",
                            OriginalAssignmentId = a.Assignment.Id, ReplacementReason = reason, AttendanceTrackingFrom = Now, IsManuallyEdited = true },
                        new DutyAssignment { DutyRosterShiftId = b.Shift.Id, EmployeeId = a.Employee.Id, Source = "Swap",
                            OriginalAssignmentId = b.Assignment.Id, ReplacementReason = reason, AttendanceTrackingFrom = Now, IsManuallyEdited = true });
                    swap.Status = "Approved"; swap.DecisionReason = reason; await TouchRequestRostersAsync(snapshots, "SwapApproved", reason);
                }
                else if (input.Action == "Reject") swap.Status = "Rejected";
                else throw new BadRequestException("Choose Approve or Reject.");
            }
            swap.Revision++; swap.DecisionReason = reason;
            await WorkflowEventAsync(swap.ParkingLotId, "Swap", id, swap.Status, before, swap, reason,
                [a.Employee.UserId, b.Employee.UserId, await RequestManagerAsync(swap.ParkingLotId)], notices);
            return swap;
        });
    }

    /// <summary>Expires vacancies past their expiry and pending swaps whose shift has started.</summary>
    public async Task<int> ProcessWorkRequestDeadlinesAsync()
    {
        var notices = new Dictionary<int, List<long>>();
        var count = await WriteAsync(async () => {
            var changed = await ExpireDueVacanciesAsync(notices);
            var swaps = await (from swap in db.WorkSwapRequests join a in db.DutyAssignments on swap.AssignmentId equals a.Id
                join s in db.DutyRosterShifts on a.DutyRosterShiftId equals s.Id
                join b in db.DutyAssignments on swap.OtherAssignmentId equals b.Id join t in db.DutyRosterShifts on b.DutyRosterShiftId equals t.Id
                where (swap.Status == "AwaitingColleague" || swap.Status == "PendingManager") && (s.StartsAt <= Now || t.StartsAt <= Now)
                orderby swap.Id select swap).Take(200).ToListAsync();
            foreach (var swap in swaps) {
                var firstAssignment = await db.DutyAssignments.FindAsync(swap.AssignmentId);
                var secondAssignment = await db.DutyAssignments.FindAsync(swap.OtherAssignmentId);
                var firstShift = await db.DutyRosterShifts.FindAsync(firstAssignment.DutyRosterShiftId);
                var secondShift = await db.DutyRosterShifts.FindAsync(secondAssignment.DutyRosterShiftId);
                if (await IsAttendanceLockedAsync(swap.ParkingLotId, firstShift.StartsAt, firstShift.EndsAt)
                    || await IsAttendanceLockedAsync(swap.ParkingLotId, secondShift.StartsAt, secondShift.EndsAt)) continue;
                if (!notices.TryGetValue(swap.ParkingLotId, out var list)) notices[swap.ParkingLotId] = list = [];
                var before = JsonSerializer.Serialize(swap); swap.Status = "Expired"; swap.Revision++;
                var users = await db.Employees.Where(e => e.Id == swap.EmployeeId || e.Id == swap.OtherEmployeeId).Select(e => e.UserId).ToListAsync();
                users.Add(await RequestManagerAsync(swap.ParkingLotId));
                await WorkflowEventAsync(swap.ParkingLotId, "Swap", swap.Id, "Expired", before, swap, "A related shift has started.", users, list); changed++;
            }
            await SaveWorkChangesAsync(); return changed;
        });
        foreach (var pair in notices) QueueAttendancePush(pair.Key, pair.Value);
        return count;
    }
}
