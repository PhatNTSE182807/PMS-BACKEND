using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.WorkScheduling;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

public partial class WorkSchedulingService
{
    private WorkRequestOptions RequestRules => requestOptions ?? new WorkRequestOptions();
    private static string RequestReason(string value) => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 1000
        ? throw new BadRequestException("A reason of 1 to 1000 characters is required.") : value.Trim();
    private record WorkContext(DutyAssignment Assignment, DutyRosterShift Shift, DutyRoster Roster, Employee Employee);

    private async Task<WorkContext> RequestContextAsync(int assignmentId, bool active = true)
    {
        var row = await (from a in db.DutyAssignments join s in db.DutyRosterShifts on a.DutyRosterShiftId equals s.Id
            join r in db.DutyRosters on s.DutyRosterId equals r.Id join e in db.Employees on a.EmployeeId equals e.Id
            join l in db.ParkingLots on r.ParkingLotId equals l.Id
            where a.Id == assignmentId && !l.IsDeleted select new { a, s, r, e }).SingleOrDefaultAsync()
            ?? throw new NotFoundException("Assignment not found.");
        if (row.r.Status != "Published" || (active && (row.a.IsCancelled || row.s.IsCancelled)))
            throw new BadRequestException("A current Published assignment is required.");
        return new(row.a, row.s, row.r, row.e);
    }

    private async Task<Employee> RequestActorAsync(int lotId)
    {
        if (scope.Role != "Staff") throw new ForbiddenException("Only Staff can submit this request.");
        var staff = await AvailableSchedulingStaffAsync(lotId);
        var person = staff.FirstOrDefault(e => e.UserId == scope.UserId) ?? throw new ForbiddenException("You are not active Staff at this lot.");
        return await db.Employees.SingleAsync(e => e.Id == person.EmployeeId);
    }

    private async Task EnsureRequestOwnerAsync(WorkContext c)
    {
        var actor = await RequestActorAsync(c.Roster.ParkingLotId);
        if (actor.Id != c.Employee.Id) throw new ForbiddenException("This is not your assignment.");
    }

    private async Task EnsureNoActualWorkAsync(int assignmentId)
    {
        if (await db.DutyAttendances.AnyAsync(a => a.DutyAssignmentId == assignmentId && (a.CheckInAt != null || a.CheckOutAt != null)))
            throw new BadRequestException("Actual work is already recorded. Resolve attendance before approving leave or swapping this assignment.");
    }

    private async Task EnsureNoPendingRequestAsync(int assignmentId)
    {
        if (await db.WorkLeaveRequests.AnyAsync(l => l.DutyAssignmentId == assignmentId && l.Status == "Pending")
            || await db.WorkSwapRequests.AnyAsync(s => (s.AssignmentId == assignmentId || s.OtherAssignmentId == assignmentId)
                && (s.Status == "AwaitingColleague" || s.Status == "PendingManager")))
            throw new ConflictException("This assignment already has a pending leave or swap request.");
    }

    private async Task<T> WorkflowAsync<T>(int lotId, Func<List<long>, Task<T>> operation)
    {
        var notices = new List<long>(); var result = await WriteAsync(() => operation(notices));
        QueueAttendancePush(lotId, notices); return result;
    }

    private async Task WorkflowEventAsync(int lotId, string kind, int id, string action, object before, object after,
        string reason, IEnumerable<string> users, List<long> notices)
    {
        db.WorkScheduleWorkflowEvents.Add(new WorkScheduleWorkflowEvent { ParkingLotId = lotId, Kind = kind, EntityId = id,
            Action = action, ActorId = string.IsNullOrEmpty(scope.UserId) ? "System" : scope.UserId, Reason = reason,
            BeforeJson = before == null ? null : JsonSerializer.Serialize(before), AfterJson = JsonSerializer.Serialize(after), CreatedAt = Now });
        var messages = users.Where(u => !string.IsNullOrEmpty(u)).Distinct().Select(u => new UserNotification {
            UserId = u, Type = "WorkRequestChanged", Title = $"Work schedule: {action}", Message = $"{kind} #{id}: {action}. Open your work schedule for details.",
            RefType = kind, RefId = id.ToString(), CreatedAt = Now, EventAt = Now,
            DataJson = JsonSerializer.Serialize(new { lotId, kind, id }) }).ToList();
        db.UserNotifications.AddRange(messages); await SaveWorkChangesAsync(); notices.AddRange(messages.Select(n => n.Id));
    }

    private async Task<string> RequestManagerAsync(int lotId) => await db.ParkingLots.Where(l => l.Id == lotId).Select(l => l.ManagerUserId).SingleAsync();
    private async Task<Dictionary<int, (DutyRoster Row, DutyRosterModel Before)>> RequestSnapshotsAsync(IEnumerable<int> rosterIds)
    {
        var result = new Dictionary<int, (DutyRoster, DutyRosterModel)>();
        foreach (var id in rosterIds.Distinct().OrderBy(x => x)) {
            var row = await db.DutyRosters.SingleAsync(r => r.Id == id); result[id] = (row, await ModelAsync(row));
        }
        return result;
    }
    private async Task TouchRequestRostersAsync(Dictionary<int, (DutyRoster Row, DutyRosterModel Before)> snapshots, string action, string reason)
    {
        await SaveWorkChangesAsync();
        foreach (var pair in snapshots.Values) {
            pair.Row.Revision++; await SaveWorkChangesAsync();
            Log(pair.Row, action, pair.Before, await ModelAsync(pair.Row), reason);
        }
        await SaveWorkChangesAsync();
    }

    public async Task<WorkLeaveRequest> CreateLeaveAsync(CreateLeaveInput input)
    {
        var initial = await RequestContextAsync(input.AssignmentId); await EnsureRequestOwnerAsync(initial);
        return await WorkflowAsync(initial.Roster.ParkingLotId, async notices => {
            var c = await RequestContextAsync(input.AssignmentId); await EnsureRequestOwnerAsync(c);
            await EnsureNoPendingRequestAsync(c.Assignment.Id); await EnsureNoActualWorkAsync(c.Assignment.Id);
            if (Now >= c.Shift.EndsAt) throw new BadRequestException("Submit leave before the assignment ends.");
            var planned = c.Shift.StartsAt - Now >= TimeSpan.FromHours(RequestRules.PlannedNoticeHours);
            if (!planned && input.ProposedEmployeeId.HasValue) throw new BadRequestException("For emergency leave, notify the Manager; direct proposals are disabled.");
            if (input.Kind != (planned ? "Planned" : "Emergency"))
                throw new BadRequestException(planned ? "Use planned leave at least 24 hours before the shift." : "Use emergency leave within 24 hours of the shift.");
            if (input.ProposedEmployeeId.HasValue && (input.ProposedEmployeeId == c.Employee.Id
                || !(await AvailableSchedulingStaffAsync(c.Roster.ParkingLotId)).Any(e => e.EmployeeId == input.ProposedEmployeeId)))
                throw new BadRequestException("Choose another active Staff member at this lot.");
            var leave = new WorkLeaveRequest { ParkingLotId = c.Roster.ParkingLotId, DutyAssignmentId = c.Assignment.Id, EmployeeId = c.Employee.Id,
                ProposedEmployeeId = input.ProposedEmployeeId, Kind = input.Kind, Reason = RequestReason(input.Reason), LeaveDate = c.Shift.WorkDate,
                StartsAt = c.Shift.StartsAt, EndsAt = c.Shift.EndsAt, CreatedAt = Now };
            db.WorkLeaveRequests.Add(leave); await SaveWorkChangesAsync();
            await WorkflowEventAsync(leave.ParkingLotId, "Leave", leave.Id, "Submitted", null, leave, leave.Reason,
                [c.Employee.UserId, await RequestManagerAsync(leave.ParkingLotId)], notices);
            return leave;
        });
    }

    public async Task<WorkLeaveRequest> DecideLeaveAsync(int id, WorkRequestCommand input)
    {
        var original = await db.WorkLeaveRequests.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id) ?? throw new NotFoundException("Leave request not found.");
        return await WorkflowAsync(original.ParkingLotId, async notices => {
            var leave = await db.WorkLeaveRequests.SingleAsync(l => l.Id == id); CheckRevision(leave.Revision, input.Revision);
            var c = await RequestContextAsync(leave.DutyAssignmentId, false);
            if (input.Action == "Cancel") await EnsureRequestOwnerAsync(c); else await EnsureManagerAsync(leave.ParkingLotId);
            if (leave.Status != "Pending") throw new ConflictException("Only a Pending leave request can be decided or cancelled.");
            var reason = input.Action == "Cancel" ? leave.Reason : RequestReason(input.Reason);
            var before = JsonSerializer.Serialize(leave);
            if (input.Action == "Approve")
            {
                c = await RequestContextAsync(leave.DutyAssignmentId); await EnsureNoActualWorkAsync(c.Assignment.Id);
                if (!(await AvailableSchedulingStaffAsync(leave.ParkingLotId)).Any(e => e.EmployeeId == c.Employee.Id)) throw new BadRequestException("Staff is no longer active at this lot.");
                var snapshots = await RequestSnapshotsAsync([c.Roster.Id]);
                var day = await db.WorkLeaveDays.SingleOrDefaultAsync(d => d.UserId == c.Employee.UserId && d.LeaveDate == leave.LeaveDate);
                if (day == null) {
                    var balance = await LeaveBalanceRowAsync(c.Employee.UserId, leave.LeaveDate.Year);
                    var used = await db.WorkLeaveDays.CountAsync(d => d.UserId == c.Employee.UserId && d.LeaveDate.Year == leave.LeaveDate.Year && d.IsPaid);
                    day = new WorkLeaveDay { UserId = c.Employee.UserId, LeaveDate = leave.LeaveDate, IsPaid = used < balance.EntitledDays, FirstLeaveRequestId = id };
                    db.WorkLeaveDays.Add(day); balance.Revision++;
                }
                leave.IsPaid = day.IsPaid; leave.Status = "Approved"; leave.DecisionReason = reason;
                var attendance = await db.DutyAttendances.SingleOrDefaultAsync(a => a.DutyAssignmentId == c.Assignment.Id);
                var priorAttendance = attendance == null ? null : DescribeAttendance(c.Assignment, c.Shift, c.Roster, true, attendance, Now);
                c.Assignment.IsCancelled = true; c.Assignment.IsManuallyEdited = true; c.Assignment.CancellationKind = "ApprovedLeave";
                if (attendance?.IsAbsent == true) {
                    var context = new AttendanceContext(c.Assignment, c.Shift, c.Roster, c.Employee, true);
                    attendance.IsAbsent = false;
                    notices.AddRange(await SaveAttendanceEventAsync(context, attendance, priorAttendance, "LeaveApproved", scope.UserId, reason));
                }
                await SaveWorkChangesAsync();
                if (Now < c.Shift.EndsAt && await StaffShortageAsync(c.Shift) > 0)
                    await NewOpenShiftAsync(c.Shift, reason, c.Assignment.Id, id, leave.ProposedEmployeeId, notices);
                await TouchRequestRostersAsync(snapshots, "LeaveApproved", reason);
            }
            else if (input.Action == "Reject") leave.Status = "Rejected";
            else if (input.Action == "Cancel") leave.Status = "Cancelled";
            else throw new BadRequestException("Choose Approve, Reject or Cancel.");
            leave.Revision++; leave.DecidedAt = Now; leave.DecisionReason = reason;
            await WorkflowEventAsync(leave.ParkingLotId, "Leave", id, leave.Status, before, leave, reason,
                [c.Employee.UserId, await RequestManagerAsync(leave.ParkingLotId)], notices);
            return leave;
        });
    }

    private async Task<WorkLeaveBalance> LeaveBalanceRowAsync(string userId, int year)
    {
        var row = await db.WorkLeaveBalances.SingleOrDefaultAsync(b => b.UserId == userId && b.Year == year);
        if (row != null) return row;
        row = new WorkLeaveBalance { UserId = userId, Year = year, EntitledDays = RequestRules.AnnualPaidDays };
        db.WorkLeaveBalances.Add(row); await SaveWorkChangesAsync(); return row;
    }
}
