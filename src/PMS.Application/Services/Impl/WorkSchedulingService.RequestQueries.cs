using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.WorkScheduling;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;
public partial class WorkSchedulingService
{
    private async Task<DutyRosterModel> DecorateRequestModelsAsync(DutyRosterModel model)
    {
        var shiftIds = model.Shifts.Select(s => s.Id).ToList();
        var assignmentIds = await db.DutyAssignments.Where(a => shiftIds.Contains(a.DutyRosterShiftId)).Select(a => a.Id).ToListAsync();
        var leaves = await db.WorkLeaveRequests.AsNoTracking().Where(l => assignmentIds.Contains(l.DutyAssignmentId)).ToListAsync();
        var swaps = await db.WorkSwapRequests.AsNoTracking().Where(s => assignmentIds.Contains(s.AssignmentId) || assignmentIds.Contains(s.OtherAssignmentId)).ToListAsync();
        var trails = await (from a in db.DutyAssignments.AsNoTracking() join e in db.Employees on a.EmployeeId equals e.Id
            where shiftIds.Contains(a.DutyRosterShiftId) && a.IsCancelled && a.CancellationKind != null
            select new { a, e.FullName }).ToListAsync();
        var coverage = await db.WorkManagerCoverages.AsNoTracking().Where(c => shiftIds.Contains(c.DutyRosterShiftId)).ToListAsync();
        var managers = await db.Employees.Where(e => coverage.Select(c => c.ManagerUserId).Contains(e.UserId)).Select(e => new { e.UserId, e.FullName }).ToListAsync();
        var slots = await OpenShiftModelsAsync(model.ParkingLotId, shiftIds);
        var assignments = await db.DutyAssignments.AsNoTracking().Where(a => shiftIds.Contains(a.DutyRosterShiftId) && !a.IsCancelled).ToDictionaryAsync(a => a.Id);
        var shifts = model.Shifts.Select(s => s with {
            Assignments = s.Assignments.Select(a => a with {
                OriginalAssignmentId = assignments[a.Id].OriginalAssignmentId, ReplacementReason = assignments[a.Id].ReplacementReason,
                AllowWeeklyOvertime = assignments[a.Id].AllowWeeklyOvertime,
                RequestState = leaves.Any(l => l.DutyAssignmentId == a.Id && l.Status == "Pending") ? "LeavePending"
                    : swaps.FirstOrDefault(w => (w.AssignmentId == a.Id || w.OtherAssignmentId == a.Id) && (w.Status == "AwaitingColleague" || w.Status == "PendingManager"))?.Status
            }).ToList(),
            Trail = trails.Where(t => t.a.DutyRosterShiftId == s.Id).Select(t => {
                var leave = leaves.FirstOrDefault(l => l.DutyAssignmentId == t.a.Id && l.Status == "Approved");
                var swap = swaps.FirstOrDefault(w => (w.AssignmentId == t.a.Id || w.OtherAssignmentId == t.a.Id) && w.Status == "Approved");
                return new AssignmentTrailModel(t.a.Id, t.a.EmployeeId, t.FullName, t.a.Position, t.a.CancellationKind,
                    leave?.Id ?? swap?.Id ?? 0, leave?.IsPaid ?? false, leave?.DecisionReason ?? swap?.DecisionReason);
            }).ToList(),
            OpenShifts = slots.Where(o => o.ShiftId == s.Id).ToList(),
            ManagerCoverage = coverage.Where(c => c.DutyRosterShiftId == s.Id).Select(c => new CoverageModel(c.Id, s.Id, c.Position,
                managers.FirstOrDefault(e => e.UserId == c.ManagerUserId)?.FullName ?? "Manager", c.Reason, c.WorkOpenShiftId)).ToList(),
            EntryAssigned = s.EntryAssigned + coverage.Count(c => c.DutyRosterShiftId == s.Id && c.Position == "Entry"),
            ExitAssigned = s.ExitAssigned + coverage.Count(c => c.DutyRosterShiftId == s.Id && c.Position == "Exit"),
            CounterAssigned = s.CounterAssigned + coverage.Count(c => c.DutyRosterShiftId == s.Id && c.Position == "Counter")
        }).ToList();
        var shortages = shifts.SelectMany(s => Positions.Select((p, i) => new ShortageModel(s.Id, s.WorkDate, s.Name, p,
            new[] { s.MinimumEntryStaff, s.MinimumExitStaff, s.MinimumCounterStaff }[i], new[] { s.EntryAssigned, s.ExitAssigned, s.CounterAssigned }[i])))
            .Where(x => x.Assigned < x.Required).ToList();
        return model with { Shifts = shifts, Shortages = shortages };
    }
    private async Task<IReadOnlyList<LeaveRequestModel>> LeaveModelsAsync(IEnumerable<WorkLeaveRequest> rows)
    {
        var list = rows.ToList(); var ids = list.Select(l => l.EmployeeId).Distinct().ToList();
        var names = await db.Employees.Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.FullName);
        return list.Select(l => new LeaveRequestModel(l.Id, l.DutyAssignmentId, l.EmployeeId, names.GetValueOrDefault(l.EmployeeId), l.Kind,
            l.Status, l.Revision, l.LeaveDate, Utc(l.StartsAt), Utc(l.EndsAt), l.Reason, l.IsPaid, l.ProposedEmployeeId, l.DecisionReason)).ToList();
    }
    private async Task<IReadOnlyList<SwapRequestModel>> SwapModelsAsync(IEnumerable<WorkSwapRequest> rows)
    {
        var list = rows.ToList(); var ids = list.SelectMany(s => new[] { s.EmployeeId, s.OtherEmployeeId }).Distinct().ToList();
        var names = await db.Employees.Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.FullName);
        return list.Select(s => new SwapRequestModel(s.Id, s.AssignmentId, s.OtherAssignmentId, s.EmployeeId, s.OtherEmployeeId,
            names.GetValueOrDefault(s.EmployeeId), names.GetValueOrDefault(s.OtherEmployeeId), s.Status, s.Revision, s.Reason, s.DecisionReason)).ToList();
    }
    private async Task<List<OpenShiftModel>> OpenShiftModelsAsync(int lotId, List<int> shiftIds = null) => await (
        from o in db.WorkOpenShifts.AsNoTracking() join s in db.DutyRosterShifts on o.DutyRosterShiftId equals s.Id
        join r in db.DutyRosters on s.DutyRosterId equals r.Id where o.ParkingLotId == lotId && !s.IsCancelled && r.Status == "Published" && (shiftIds == null || shiftIds.Contains(s.Id))
        orderby o.Id descending select new OpenShiftModel(o.Id, s.Id, o.Position, o.Status, o.Stage, o.Revision, o.OriginalAssignmentId,
            o.TargetEmployeeId, o.DeadlineAt == null ? null : Utc(o.DeadlineAt.Value), o.ReplacementAssignmentId,
            Utc(s.StartsAt), Utc(s.EndsAt), s.WorkDate, s.Name)).ToListAsync();

    private async Task<HashSet<int>> VisibleSlotIdsAsync(int lotId, int employeeId)
    {
        var claimed = await db.WorkOpenShiftClaims.Where(c => c.EmployeeId == employeeId).Select(c => c.WorkOpenShiftId).ToListAsync();
        return (await db.WorkOpenShifts.Where(o => o.ParkingLotId == lotId && ((o.Status == "Open" && (o.Stage == "Public" || o.TargetEmployeeId == employeeId)) || claimed.Contains(o.Id)))
            .Select(o => o.Id).ToListAsync()).ToHashSet();
    }
    private async Task<List<LeaveBalanceModel>> BalanceModelsAsync(IEnumerable<Employee> employees, int year)
    {
        if (year is < 1900 or > 9998) throw new BadRequestException("Choose a valid leave year.");
        var people = employees.ToList(); var users = people.Select(e => e.UserId).ToList();
        var balances = await db.WorkLeaveBalances.AsNoTracking().Where(b => users.Contains(b.UserId) && b.Year == year).ToListAsync();
        var days = await db.WorkLeaveDays.AsNoTracking().Where(d => users.Contains(d.UserId) && d.LeaveDate.Year == year).ToListAsync();
        return people.Select(e => { var balance = balances.FirstOrDefault(b => b.UserId == e.UserId);
            return new LeaveBalanceModel(e.Id, e.FullName, year, balance?.EntitledDays ?? RequestRules.AnnualPaidDays,
                days.Count(d => d.UserId == e.UserId && d.IsPaid), days.Count(d => d.UserId == e.UserId && !d.IsPaid), balance?.Revision ?? 0); }).ToList();
    }
    public async Task<WorkRequestsModel> RequestsAsync(int lotId, int year)
    {
        Employee actor = null;
        if (scope.Role == "Manager") await EnsureManagerAsync(lotId); else actor = await RequestActorAsync(lotId);
        var leaves = await db.WorkLeaveRequests.AsNoTracking().Where(l => l.ParkingLotId == lotId && (actor == null || l.EmployeeId == actor.Id))
            .OrderByDescending(l => l.Status == "Pending").ThenByDescending(l => l.Id).Take(300).ToListAsync();
        var swaps = await db.WorkSwapRequests.AsNoTracking().Where(s => s.ParkingLotId == lotId && (actor == null || s.EmployeeId == actor.Id || s.OtherEmployeeId == actor.Id))
            .OrderByDescending(s => s.Status == "AwaitingColleague" || s.Status == "PendingManager").ThenByDescending(s => s.Id).Take(300).ToListAsync();
        var slots = await OpenShiftModelsAsync(lotId);
        if (actor != null) { var visible = await VisibleSlotIdsAsync(lotId, actor.Id); slots = slots.Where(s => visible.Contains(s.Id)).ToList(); }
        var staffUsers = await identity.Users.Where(u => u.Role == "Staff").Select(u => u.Id).ToListAsync();
        var employees = await db.Employees.Where(e => e.ParkingLotId == lotId && staffUsers.Contains(e.UserId) && (actor == null || e.Id == actor.Id)).ToListAsync();
        return new(await LeaveModelsAsync(leaves), await SwapModelsAsync(swaps), slots.Take(300).ToList(), await BalanceModelsAsync(employees, year), RequestRules);
    }

    public async Task<AssignmentRequestDetailModel> AssignmentRequestsAsync(int assignmentId)
    {
        var c = await RequestContextAsync(assignmentId, false);
        if (scope.Role == "Manager") await EnsureManagerAsync(c.Roster.ParkingLotId); else await EnsureRequestOwnerAsync(c);
        var leaves = await db.WorkLeaveRequests.AsNoTracking().Where(l => l.DutyAssignmentId == assignmentId).OrderByDescending(l => l.Id).ToListAsync();
        var swaps = await db.WorkSwapRequests.AsNoTracking().Where(s => s.AssignmentId == assignmentId || s.OtherAssignmentId == assignmentId).OrderByDescending(s => s.Id).ToListAsync();
        var available = await AvailableSchedulingStaffAsync(c.Roster.ParkingLotId); var eligibleIds = available.Select(e => e.EmployeeId).ToList();
        var canRequest = scope.Role == "Staff" && !c.Assignment.IsCancelled && !c.Shift.IsCancelled && Now < c.Shift.EndsAt
            && !await db.DutyAttendances.AnyAsync(a => a.DutyAssignmentId == assignmentId && (a.CheckInAt != null || a.CheckOutAt != null))
            && !leaves.Any(l => l.Status == "Pending") && !swaps.Any(s => s.Status is "AwaitingColleague" or "PendingManager");
        var limit = Now.AddDays(93);
        var candidates = canRequest ? await (from a in db.DutyAssignments join s in db.DutyRosterShifts on a.DutyRosterShiftId equals s.Id
            join r in db.DutyRosters on s.DutyRosterId equals r.Id join e in db.Employees on a.EmployeeId equals e.Id
            where !a.IsCancelled && !s.IsCancelled && r.Status == "Published" && r.ParkingLotId == c.Roster.ParkingLotId
                && e.UserId != c.Employee.UserId && eligibleIds.Contains(e.Id) && s.StartsAt > Now && s.StartsAt <= limit
            orderby s.StartsAt, a.Id select new SwapCandidateModel(a.Id, e.Id, e.FullName, s.Name, s.WorkDate, Utc(s.StartsAt), Utc(s.EndsAt), a.Position)).Take(200).ToListAsync() : [];
        return new(assignmentId, c.Roster.ParkingLotId, c.Employee.Id, c.Employee.FullName, c.Assignment.CancellationKind ?? (c.Assignment.IsCancelled ? "Cancelled" : "Assigned"),
            Utc(c.Shift.StartsAt), Utc(c.Shift.EndsAt), canRequest, available, candidates, await LeaveModelsAsync(leaves), await SwapModelsAsync(swaps));
    }

    public async Task<OpenShiftDetailModel> OpenShiftDetailAsync(int id)
    {
        var row = await db.WorkOpenShifts.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id) ?? throw new NotFoundException("Open shift not found.");
        Employee actor = null;
        if (scope.Role == "Manager") await EnsureManagerAsync(row.ParkingLotId);
        else { actor = await RequestActorAsync(row.ParkingLotId); if (!(await VisibleSlotIdsAsync(row.ParkingLotId, actor.Id)).Contains(id)) throw new ForbiddenException("This offer is not available to you."); }
        var slot = (await OpenShiftModelsAsync(row.ParkingLotId)).SingleOrDefault(s => s.Id == id) ?? throw new NotFoundException("Open shift not found.");
        var candidates = new List<RosterStaffModel>();
        var shift = await db.DutyRosterShifts.SingleAsync(s => s.Id == slot.ShiftId);
        if (actor == null) candidates = await EligibleReplacementsAsync(row.ParkingLotId, shift);
        var claims = await (from claim in db.WorkOpenShiftClaims.AsNoTracking() join e in db.Employees on claim.EmployeeId equals e.Id
            where claim.WorkOpenShiftId == id && (actor == null || claim.EmployeeId == actor.Id)
            orderby claim.Id select new OpenShiftClaimModel(claim.Id, e.Id, e.FullName, claim.Status)).ToListAsync();
        var standby = actor == null ? await (from s in db.DutyStandbyAssignments join r in db.DutyRosters on s.DutyRosterId equals r.Id
            join e in db.Employees on s.EmployeeId equals e.Id where !s.IsCancelled && r.ParkingLotId == row.ParkingLotId && s.WorkDate == shift.WorkDate
            select new StandbyModel(s.Id, s.WorkDate, s.EmployeeId, e.FullName, s.Source)).ToListAsync() : [];
        var events = actor == null ? await db.WorkScheduleWorkflowEvents.AsNoTracking().Where(e => e.ParkingLotId == row.ParkingLotId && e.Kind == "OpenShift" && e.EntityId == id)
            .OrderByDescending(e => e.Id).Select(e => new WorkflowEventModel(e.Id, e.Action, e.ActorId, e.Reason, Utc(e.CreatedAt), e.BeforeJson, e.AfterJson)).ToListAsync() : [];
        return new(slot, candidates, claims, standby, events, actor == null ? await AvailableSchedulingStaffAsync(row.ParkingLotId) : []);
    }

    public async Task<IReadOnlyList<LeaveBalanceModel>> AdminLeaveBalancesAsync(int year)
    {
        if (scope.Role != "Admin") throw new ForbiddenException("Only Admin manages leave entitlements.");
        var users = await identity.Users.Where(u => u.Role == "Staff").Select(u => u.Id).ToListAsync();
        return await BalanceModelsAsync(await db.Employees.Where(e => users.Contains(e.UserId)).ToListAsync(), year);
    }
    public async Task<LeaveBalanceModel> SetLeaveEntitlementAsync(int employeeId, int year, LeaveEntitlementInput input)
    {
        if (scope.Role != "Admin") throw new ForbiddenException("Only Admin manages leave entitlements.");
        var employee = await db.Employees.SingleOrDefaultAsync(e => e.Id == employeeId) ?? throw new NotFoundException("Employee not found.");
        return await WorkflowAsync(employee.ParkingLotId ?? throw new BadRequestException("Employee has no lot."), async notices => {
            if (year is < 1900 or > 9998 || input.EntitledDays < 0 || input.EntitledDays > 366) throw new BadRequestException("Choose a valid year and entitlement.");
            var reason = RequestReason(input.Reason);
            var row = await db.WorkLeaveBalances.SingleOrDefaultAsync(b => b.UserId == employee.UserId && b.Year == year);
            CheckRevision(row?.Revision ?? 0, input.Revision);
            var used = await db.WorkLeaveDays.CountAsync(d => d.UserId == employee.UserId && d.LeaveDate.Year == year && d.IsPaid);
            if (input.EntitledDays < used) throw new BadRequestException("Entitlement cannot be below paid leave already used.");
            var before = row == null ? null : JsonSerializer.Serialize(row);
            row ??= await LeaveBalanceRowAsync(employee.UserId, year); row.EntitledDays = input.EntitledDays; row.Revision++;
            await WorkflowEventAsync(employee.ParkingLotId.Value, "LeaveBalance", row.Id, "EntitlementChanged", before, row, reason, [employee.UserId], notices);
            return (await BalanceModelsAsync([employee], year)).Single();
        });
    }
}
