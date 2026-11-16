using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.WorkScheduling;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;
public partial class WorkSchedulingService
{
    private static decimal Hours(DateTime? start, DateTime? end, DateTime from, DateTime to)
    {
        if (!start.HasValue || !end.HasValue) return 0;
        var a = start.Value > from ? start.Value : from;
        var b = end.Value < to ? end.Value : to;
        return b > a ? (decimal)(b - a).TotalHours : 0;
    }

    private async Task<AttendancePayReview> PayReviewAsync(AttendanceContext c, DutyAttendance record,
        DateTime? from = null, DateTime? to = null, List<DutyAttendanceEvent> loadedEvents = null,
        List<AttendanceOvertimeDecision> loadedDecisions = null, List<AttendancePeriodLot> loadedPeriods = null, List<ActualWork> loadedWork = null)
    {
        var events = loadedEvents ?? await db.DutyAttendanceEvents.AsNoTracking().Where(e => e.DutyAssignmentId == c.Assignment.Id).OrderBy(e => e.Revision).ToListAsync();
        DateTime? originalIn = null, originalOut = null;
        foreach (var e in events.Where(e => e.Action is "Started" or "Ended")) {
            try {
                var state = JsonSerializer.Deserialize<AttendanceModel>(e.AfterJson);
                if (e.Action == "Started") originalIn = state?.CheckInAt;
                if (e.Action == "Ended") originalOut = state?.CheckOutAt;
            } catch (JsonException) { /* Unknown historic evidence stays unknown. */ }
        }
        var begin = from ?? DateTime.MinValue; var end = to ?? DateTime.MaxValue;
        var adjusted = Hours(record?.CheckInAt, record?.CheckOutAt, begin, end);
        var work = loadedWork ?? await ActualWorkAsync([c.Employee.UserId], c.Shift.StartsAt.AddDays(-8), c.Shift.EndsAt.AddDays(8));
        var ordinary = OrdinaryWork(c, record, work);
        var regular = ordinary.Regular.Sum(x => Hours(x.Start, x.End, begin, end));
        var overtime = Math.Max(0, adjusted - regular);
        var decisions = loadedDecisions ?? await db.AttendanceOvertimeDecisions.AsNoTracking().Where(d => d.DutyAssignmentId == c.Assignment.Id).ToListAsync();
        var decision = decisions.Where(d => d.DutyAssignmentId == c.Assignment.Id && d.AttendanceRevision == (record?.Revision ?? 0)
            && d.PlannedStart == c.Shift.StartsAt && d.PlannedEnd == c.Shift.EndsAt && d.CalculationKey == ordinary.Key).OrderByDescending(d => d.Id).FirstOrDefault();
        var allPeriods = loadedPeriods ?? await db.AttendancePeriodLots.AsNoTracking().Where(p => p.ParkingLotId == c.Roster.ParkingLotId).ToListAsync();
        var periods = allPeriods.Where(p => (c.Shift.StartsAt < p.EndsAt && p.StartsAt < c.Shift.EndsAt)
                || (record?.CheckInAt < p.EndsAt && p.StartsAt < record?.CheckOutAt)).ToList();
        var locked = periods.Any(p => p.Status == "Locked");
        var reviewed = record != null && record.ReviewedRevision == record.Revision;
        var approvedRegular = reviewed ? regular : periods.Where(p => p.Status is "Confirmed" or "Locked").Sum(p =>
            ordinary.Regular.Sum(x => Hours(x.Start, x.End, p.StartsAt > begin ? p.StartsAt : begin, p.EndsAt < end ? p.EndsAt : end)));
        var approved = approvedRegular + (decision?.Approved == true ? overtime : 0);
        return new(originalIn.HasValue ? Utc(originalIn.Value) : null, originalOut.HasValue ? Utc(originalOut.Value) : null,
            originalIn.HasValue && originalOut.HasValue ? Hours(originalIn, originalOut, begin, end) : null,
            adjusted, regular, overtime, overtime == 0 ? "None" : decision == null ? "Pending" : decision.Approved ? "Approved" : "Rejected",
            approved, events.Any(e => e.Action is "Adjusted" or "Reopened"), reviewed, locked,
            periods.Select(p => $"#{p.AttendancePeriodId} · {p.Status}").ToList(), decision?.Id ?? 0) {
                OvertimeHistory = decisions.Where(d => d.DutyAssignmentId == c.Assignment.Id).OrderByDescending(d => d.Id).ToList()
            };
    }

    public async Task<AttendancePeriodReview> ReviewPeriodAsync(int periodId, int lotId)
    {
        if (scope.Role == "Admin") await scope.EnsureCanManageAsync(lotId); else await EnsureManagerAsync(lotId);
        var p = await db.AttendancePeriodLots.SingleOrDefaultAsync(x => x.AttendancePeriodId == periodId && x.ParkingLotId == lotId)
            ?? throw new NotFoundException("This lot is not included in the attendance period.");
        var result = p.Status == "Locked" && p.SnapshotJson != null
            ? JsonSerializer.Deserialize<AttendancePeriodReview>(p.SnapshotJson) : await BuildPeriodReviewAsync(p);
        if (scope.Role != "Admin") result = result with { Period = result.Period with { Lots = result.Period.Lots.Where(l => l.ParkingLotId == lotId).ToList() } };
        return result;
    }

    private async Task<AttendancePeriodReview> BuildPeriodReviewAsync(AttendancePeriodLot p)
    {
        var period = await db.AttendancePeriods.SingleAsync(x => x.Id == p.AttendancePeriodId);
        var ids = await (from a in db.DutyAssignments join s in db.DutyRosterShifts on a.DutyRosterShiftId equals s.Id
            join r in db.DutyRosters on s.DutyRosterId equals r.Id
            join at in db.DutyAttendances on a.Id equals at.DutyAssignmentId into ats from at in ats.DefaultIfEmpty()
            where r.ParkingLotId == p.ParkingLotId && r.Status == "Published" && !s.IsCancelled
                && (!a.IsCancelled || a.CancellationKind != null)
                && ((s.StartsAt < p.EndsAt && p.StartsAt < s.EndsAt) || (at != null && at.CheckInAt < p.EndsAt && p.StartsAt < at.CheckOutAt))
            orderby s.StartsAt select a.Id).ToListAsync();
        var contexts = await (from a in db.DutyAssignments.AsNoTracking() join s in db.DutyRosterShifts.AsNoTracking() on a.DutyRosterShiftId equals s.Id
            join r in db.DutyRosters.AsNoTracking() on s.DutyRosterId equals r.Id join e in db.Employees.AsNoTracking() on a.EmployeeId equals e.Id
            where ids.Contains(a.Id) select new { a, s, r, e }).ToListAsync();
        var userIds = contexts.Select(x => x.e.UserId).Distinct().ToList();
        var activeUsers = await identity.Users.Where(u => userIds.Contains(u.Id) && u.IsActive && u.Role == "Staff").Select(u => u.Id).ToListAsync();
        var records = await db.DutyAttendances.AsNoTracking().Where(x => ids.Contains(x.DutyAssignmentId)).ToDictionaryAsync(x => x.DutyAssignmentId);
        var allEvents = (await db.DutyAttendanceEvents.AsNoTracking().Where(x => ids.Contains(x.DutyAssignmentId)).OrderBy(x => x.Revision).ToListAsync()).ToLookup(x => x.DutyAssignmentId);
        var allDecisions = (await db.AttendanceOvertimeDecisions.AsNoTracking().Where(x => ids.Contains(x.DutyAssignmentId)).ToListAsync()).ToLookup(x => x.DutyAssignmentId);
        var allPeriods = await db.AttendancePeriodLots.AsNoTracking().Where(x => x.ParkingLotId == p.ParkingLotId).ToListAsync();
        var allWork = await ActualWorkAsync(userIds, p.StartsAt.AddDays(-8), p.EndsAt.AddDays(8));
        var rows = new List<AttendanceReviewRow>();
        foreach (var item in contexts.OrderBy(x => x.s.StartsAt).ThenBy(x => x.a.Id)) {
            var id = item.a.Id;
            var c = new AttendanceContext(item.a, item.s, item.r, item.e, item.e.Status == "Active" && item.e.ParkingLotId == p.ParkingLotId && activeUsers.Contains(item.e.UserId));
            var record = records.GetValueOrDefault(id);
            rows.Add(new(id, c.Employee.Id, c.Employee.FullName, c.Shift.Name, c.Shift.WorkDate, Utc(c.Shift.StartsAt), Utc(c.Shift.EndsAt),
                DescribeAttendance(c.Assignment, c.Shift, c.Roster, c.StaffAvailable, record, Now),
                await PayReviewAsync(c, record, p.StartsAt, p.EndsAt, allEvents[id].ToList(), allDecisions[id].ToList(), allPeriods, allWork)));
        }
        var employeeIds = rows.Select(r => r.EmployeeId).ToList();
        var leaves = await db.WorkLeaveRequests.AsNoTracking().Where(x => x.ParkingLotId == p.ParkingLotId && x.Status == "Approved"
            && x.LeaveDate >= period.FromDate && x.LeaveDate <= period.ToDate).ToListAsync();
        var carry = await db.AttendanceCarryAdjustments.AsNoTracking().Where(x => x.TargetPeriodLotId == p.Id).ToListAsync();
        employeeIds.AddRange(leaves.Select(x => x.EmployeeId)); employeeIds.AddRange(carry.Select(x => x.EmployeeId));
        var employees = await db.Employees.AsNoTracking().Where(x => employeeIds.Contains(x.Id)).ToListAsync();
        var summary = employees.Select(e => {
            var own = rows.Where(r => r.EmployeeId == e.Id).ToList();
            var days = leaves.Where(l => l.EmployeeId == e.Id).GroupBy(l => l.LeaveDate).ToList();
            var adjustmentHours = carry.Where(x => x.EmployeeId == e.Id).Sum(x => x.Hours);
            return new AttendanceEmployeeSummary(e.Id, e.FullName,
                own.Where(r => r.Attendance.Status != "Reassigned").Sum(r => Hours(r.StartsAt, r.EndsAt, p.StartsAt, p.EndsAt)),
                own.Any(r => r.Pay.OriginalHours == null && r.Attendance.Status is "Completed" or "Working") ? null : own.Sum(r => r.Pay.OriginalHours ?? 0),
                own.Sum(r => r.Pay.AdjustedHours), own.Sum(r => r.Pay.ApprovedHours) + adjustmentHours,
                own.Sum(r => r.Pay.OvertimeHours), own.Where(r => r.Pay.OvertimeStatus == "Approved").Sum(r => r.Pay.OvertimeHours),
                days.Count(g => g.Any(l => l.IsPaid)), days.Count(g => !g.Any(l => l.IsPaid)),
                own.Count(r => r.Attendance.Status == "Absent" && r.WorkDate >= period.FromDate && r.WorkDate <= period.ToDate), adjustmentHours);
        }).OrderBy(x => x.StaffName).ToList();
        var warnings = new List<string>();
        var weeklyFrom = DayBounds(WeekStart(period.FromDate), RotationZone(p.TimeZoneId)).Start;
        if (await (from at in db.DutyAttendances join a in db.DutyAssignments on at.DutyAssignmentId equals a.Id
            join s in db.DutyRosterShifts on a.DutyRosterShiftId equals s.Id join e in db.Employees on a.EmployeeId equals e.Id
            join r in db.DutyRosters on s.DutyRosterId equals r.Id
            where userIds.Contains(e.UserId) && r.Status == "Published" && !a.IsCancelled && !s.IsCancelled
                && at.CheckInAt != null && at.CheckOutAt == null && s.StartsAt < p.StartsAt && weeklyFrom < s.EndsAt select a.Id).AnyAsync())
            warnings.Add("Earlier unfinished attendance in this week's calculation needs resolution.");
        if (p.EndsAt > Now) warnings.Add("Period has not ended.");
        if (rows.Any(r => r.Attendance.Status is "Working" or "StartWindow" or "Upcoming")) warnings.Add("Assignments still await completion.");
        if (rows.Any(r => r.Attendance.AutoEnded && !r.Pay.Reviewed)) warnings.Add("Auto-ended shifts need individual review.");
        if (rows.Any(r => r.Pay.OvertimeStatus == "Pending")) warnings.Add("Overtime needs an approve/reject decision.");
        if (rows.Any(r => r.Attendance.Status is "Untracked" or "Unavailable" or "StartClosed" && !r.Pay.Reviewed)) warnings.Add("Untracked or unavailable attendance needs individual review.");
        if (await db.WorkLeaveRequests.AnyAsync(x => x.ParkingLotId == p.ParkingLotId && x.Status == "Pending" && x.LeaveDate >= period.FromDate && x.LeaveDate <= period.ToDate))
            warnings.Add("Pending leave requests must be resolved.");
        if (await (from o in db.WorkOpenShifts join s in db.DutyRosterShifts on o.DutyRosterShiftId equals s.Id
            where o.ParkingLotId == p.ParkingLotId && o.Status == "Open" && s.StartsAt < p.EndsAt && p.StartsAt < s.EndsAt select o.Id).AnyAsync())
            warnings.Add("Open replacement offers must be closed or expired.");
        if (await (from w in db.WorkSwapRequests join a in db.DutyAssignments on w.AssignmentId equals a.Id
            join s in db.DutyRosterShifts on a.DutyRosterShiftId equals s.Id
            join b in db.DutyAssignments on w.OtherAssignmentId equals b.Id join t in db.DutyRosterShifts on b.DutyRosterShiftId equals t.Id
            where w.ParkingLotId == p.ParkingLotId && (w.Status == "AwaitingColleague" || w.Status == "PendingManager")
                && ((s.StartsAt < p.EndsAt && p.StartsAt < s.EndsAt) || (t.StartsAt < p.EndsAt && p.StartsAt < t.EndsAt)) select w.Id).AnyAsync())
            warnings.Add("Pending swap requests must be resolved.");
        var managerDays = await db.ManagerWorkDays.AsNoTracking().Where(x => x.ParkingLotId == p.ParkingLotId && x.WorkDate >= period.FromDate && x.WorkDate <= period.ToDate).OrderBy(x => x.WorkDate).ToListAsync();
        var managerIds = managerDays.Select(x => x.ManagerUserId).Distinct().ToList();
        var managerNames = await db.Employees.AsNoTracking().Where(e => managerIds.Contains(e.UserId)).Select(e => new { e.UserId, e.FullName }).ToListAsync();
        var managerModels = managerDays.Select(d => new ManagerWorkDayModel(d.Id, d.ParkingLotId, d.ManagerUserId,
            managerNames.FirstOrDefault(e => e.UserId == d.ManagerUserId)?.FullName ?? "Manager", d.WorkDate, d.Kind, d.Reason, d.Revision)).ToList();
        var history = await db.AttendancePeriodEvents.AsNoTracking().Where(x => x.AttendancePeriodLotId == p.Id).OrderByDescending(x => x.Id).ToListAsync();
        var model = await PeriodModelAsync(period);
        return new(model, model.Lots.Single(x => x.Id == p.Id), rows, summary, warnings, managerModels, carry, history);
    }

    public Task<AttendanceDetailModel> ReviewAssignmentAsync(int id, AttendanceMutationInput input) =>
        ChangeAttendanceAsync(id, input, "Reviewed", true, (c, record, state) => {
            if (state.Status is "Working" or "Upcoming" or "StartWindow") throw new BadRequestException("Complete the shift before reviewing attendance.");
            // The calendar can infer due states while a timer is delayed. Persist the reviewed
            // result before confirmation so locking cannot strand an unfinished underlying row.
            if (state.AutoEnded && record.CheckInAt.HasValue && !record.CheckOutAt.HasValue) {
                record.CheckOutAt = state.CheckOutAt; record.AutoEnded = true;
            }
            if (state.Status == "Absent") record.IsAbsent = true;
            record.ReviewedRevision = record.Revision + 1;
            return Task.CompletedTask;
        });

    public async Task<AttendanceDetailModel> DecideOvertimeAsync(int id, OvertimeDecisionInput input)
    {
        var notices = new List<long>();
        var lotId = await WriteAsync(async () => {
            var c = await LoadAttendanceContextAsync(id); await EnsureAttendanceAccessAsync(c, true);
            var at = await db.DutyAttendances.SingleOrDefaultAsync(x => x.DutyAssignmentId == id);
            CheckRevision(at?.Revision ?? 0, input.Revision); CheckRevision(c.Roster.Revision, input.RosterRevision);
            var pay = await PayReviewAsync(c, at);
            CheckRevision(pay.OvertimeDecisionId, input.DecisionId);
            if (at?.CheckOutAt == null || pay.OvertimeHours <= 0) throw new BadRequestException("Completed attendance with overtime is required.");
            var decision = new AttendanceOvertimeDecision { DutyAssignmentId = id, AttendanceRevision = at.Revision,
                CalculationKey = OrdinaryWork(c, at, await ActualWorkAsync([c.Employee.UserId], c.Shift.StartsAt.AddDays(-8), c.Shift.EndsAt.AddDays(8))).Key,
                PlannedStart = c.Shift.StartsAt, PlannedEnd = c.Shift.EndsAt, Approved = input.Approved,
                Reason = RequestReason(input.Reason), DecidedBy = scope.UserId, CreatedAt = Now };
            db.AttendanceOvertimeDecisions.Add(decision); await SaveWorkChangesAsync();
            var periods = await db.AttendancePeriodLots.Where(p => p.ParkingLotId == c.Roster.ParkingLotId).ToListAsync();
            foreach (var p in periods.Where(p => (c.Shift.StartsAt < p.EndsAt && p.StartsAt < c.Shift.EndsAt)
                || (at.CheckInAt < p.EndsAt && p.StartsAt < at.CheckOutAt)))
                PeriodEvent(p, "OvertimeDecided", decision.Reason, pay, decision);
            await WorkflowEventAsync(c.Roster.ParkingLotId, "Overtime", id, input.Approved ? "Approved" : "Rejected", pay, decision, decision.Reason,
                [c.Employee.UserId], notices);
            return c.Roster.ParkingLotId;
        });
        QueueAttendancePush(lotId, notices); return await AttendanceDetailAsync(id);
    }
}
