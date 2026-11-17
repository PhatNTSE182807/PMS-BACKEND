using System.Globalization;
using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PMS.Application.Exceptions;
using PMS.Application.Jobs;
using PMS.Application.Models.WorkScheduling;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

public partial class WorkSchedulingService
{
    private readonly WorkAttendanceOptions attendanceRules = attendanceOptions ?? new WorkAttendanceOptions();
    private record AttendanceContext(DutyAssignment Assignment, DutyRosterShift Shift, DutyRoster Roster,
        Employee Employee, bool StaffAvailable);

    private AttendanceModel DescribeAttendance(DutyAssignment a, DutyRosterShift s, DutyRoster roster,
        bool available, DutyAttendance record, DateTime now)
    {
        var replacementStart = a.Source == "Replacement" && a.AttendanceTrackingFrom > s.StartsAt;
        var startReference = replacementStart ? a.AttendanceTrackingFrom : s.StartsAt;
        var opens = replacementStart ? startReference : s.StartsAt.AddMinutes(-attendanceRules.EarlyStartMinutes);
        var late = startReference.AddMinutes(attendanceRules.LateAfterMinutes);
        var absent = startReference.AddMinutes(attendanceRules.AbsentAfterMinutes);
        var grace = s.EndsAt.AddMinutes(attendanceRules.EndGraceMinutes);
        var checkIn = record?.CheckInAt;
        var checkOut = record?.CheckOutAt;
        var autoEnded = record?.AutoEnded ?? false;
        string status;
        if (a.IsCancelled && a.CancellationKind == "ApprovedLeave") status = "ApprovedLeave";
        else if (a.IsCancelled) status = "Reassigned";
        else if (roster.Status != "Published") status = "NotPublished";
        else if (checkOut.HasValue) status = "Completed";
        else if (checkIn.HasValue)
        {
            if (now > grace)
            {
                status = "Completed";
                checkOut = checkIn > s.EndsAt ? checkIn : s.EndsAt;
                autoEnded = true;
            }
            else status = "Working";
        }
        else if (record?.IsAbsent == true) status = "Absent";
        else if (!available) status = "Unavailable";
        else if (a.AttendanceTrackingFrom > absent || !roster.PublishedAt.HasValue || roster.PublishedAt > absent) status = "Untracked";
        else if (now > absent) status = "Absent";
        else if (now < opens) status = "Upcoming";
        else if (now >= s.EndsAt) status = "StartClosed";
        else status = "StartWindow";
        return new AttendanceModel(status, record?.Revision ?? 0, checkIn.HasValue ? Utc(checkIn.Value) : null,
            checkOut.HasValue ? Utc(checkOut.Value) : null, record?.IsLate ?? false, autoEnded,
            available && status == "StartWindow", available && status == "Working",
            available && status == "Absent" && now < s.EndsAt,
            Utc(opens), Utc(late), Utc(absent), Utc(grace), Utc(now));
    }

    private async Task<AttendanceContext> LoadAttendanceContextAsync(int assignmentId, bool historical = false)
    {
        var row = await (from a in db.DutyAssignments
            join s in db.DutyRosterShifts on a.DutyRosterShiftId equals s.Id
            join r in db.DutyRosters on s.DutyRosterId equals r.Id
            join e in db.Employees on a.EmployeeId equals e.Id
            join l in db.ParkingLots on r.ParkingLotId equals l.Id
            where a.Id == assignmentId && (!a.IsCancelled || (historical && a.CancellationKind != null)) && !s.IsCancelled && !l.IsDeleted
            select new { Assignment = a, Shift = s, Roster = r, Employee = e }).SingleOrDefaultAsync()
            ?? throw new NotFoundException("Assignment not found.");
        var available = row.Employee.Status == "Active" && row.Employee.ParkingLotId == row.Roster.ParkingLotId
            && await identity.Users.AnyAsync(u => u.Id == row.Employee.UserId && u.IsActive && u.Role == "Staff");
        return new AttendanceContext(row.Assignment, row.Shift, row.Roster, row.Employee, available);
    }

    private async Task EnsureAttendanceAccessAsync(AttendanceContext c, bool managerOnly = false)
    {
        if (scope.Role == "Manager") { await EnsureManagerAsync(c.Roster.ParkingLotId); return; }
        if (!managerOnly && scope.Role == "Staff" && c.Employee.UserId == scope.UserId && c.StaffAvailable && c.Roster.Status == "Published") return;
        throw new ForbiddenException("You cannot access attendance for this assignment.");
    }

    public async Task<AttendanceDetailModel> AttendanceDetailAsync(int assignmentId)
    {
        var c = await LoadAttendanceContextAsync(assignmentId, true);
        await EnsureAttendanceAccessAsync(c);
        return await AttendanceDetailAsync(c);
    }

    private async Task<AttendanceDetailModel> AttendanceDetailAsync(AttendanceContext c)
    {
        var record = await db.DutyAttendances.AsNoTracking().SingleOrDefaultAsync(x => x.DutyAssignmentId == c.Assignment.Id);
        var history = await db.DutyAttendanceEvents.AsNoTracking().Where(e => e.DutyAssignmentId == c.Assignment.Id)
            .OrderByDescending(e => e.Revision).ToListAsync();
        var actors = history.Select(e => e.ActorId).Distinct().ToList();
        var names = await db.Employees.AsNoTracking().Where(e => actors.Contains(e.UserId)).Select(e => new { e.UserId, e.FullName }).ToListAsync();
        var pay = await PayReviewAsync(c, record);
        var state = DescribeAttendance(c.Assignment, c.Shift, c.Roster, c.StaffAvailable, record, Now);
        if (pay.Locked) state = state with { CanStart = false, CanEnd = false, CanReopen = false };
        return new AttendanceDetailModel(c.Assignment.Id, c.Employee.Id, c.Employee.FullName, c.Shift.Name,
            c.Roster.ParkingLotId, c.Roster.Id, c.Roster.Name, c.Roster.Status, c.Roster.Revision,
            Utc(c.Shift.StartsAt), Utc(c.Shift.EndsAt), c.Shift.TimeZoneId,
            state,
            history.Select(e => new AttendanceEventModel(e.Id, e.Revision, e.Action,
                e.ActorId == "System" ? "System" : names.FirstOrDefault(n => n.UserId == e.ActorId)?.FullName ?? "Manager",
                e.Reason, Utc(e.CreatedAt), e.BeforeJson, e.AfterJson)).ToList()) { PayReview = pay };
    }

    public async Task<IReadOnlyList<AttendanceDetailModel>> CurrentAssignmentsAsync()
    {
        if (scope.Role != "Staff") throw new ForbiddenException("Only Staff have a current shift.");
        var now = Now;
        var end = now.AddDays(2);
        var start = now.AddDays(-1);
        var ids = await (from a in db.DutyAssignments
            join s in db.DutyRosterShifts on a.DutyRosterShiftId equals s.Id
            join r in db.DutyRosters on s.DutyRosterId equals r.Id
            join e in db.Employees on a.EmployeeId equals e.Id
            where !a.IsCancelled && !s.IsCancelled && r.Status == "Published" && e.UserId == scope.UserId
                && e.Status == "Active" && e.ParkingLotId == r.ParkingLotId && s.EndsAt >= start && s.StartsAt <= end
            orderby s.StartsAt select a.Id).ToListAsync();
        var result = new List<AttendanceDetailModel>();
        foreach (var id in ids)
        {
            var c = await LoadAttendanceContextAsync(id);
            if (c.StaffAvailable) result.Add(await AttendanceDetailAsync(c));
        }
        // Keep a recently missed/completed shift visible, as well as the active and next shifts.
        return result.Where(x => x.Attendance.Status == "Working" || x.PlannedEnd >= now.AddHours(-12))
            .OrderBy(x => x.Attendance.Status == "Working" ? 0 : x.Attendance.CanStart ? 1 : x.Attendance.Status == "Upcoming" ? 2 : 3)
            .ThenBy(x => x.PlannedStart).Take(6).ToList();
    }

    public Task<AttendanceDetailModel> StartAssignmentAsync(int id, AttendanceMutationInput input) =>
        ChangeAttendanceAsync(id, input, "Started", false, async (c, record, state) =>
        {
            if (scope.Role != "Staff" || scope.UserId != c.Employee.UserId) throw new ForbiddenException("Only the assigned Staff can start this shift.");
            if (!state.CanStart) throw new BadRequestException("The start window is closed. A missed shift requires the Manager to reopen it.");
            await EnsureNoOtherWorkingAsync(c.Employee.UserId, id);
            record.CheckInAt = Now;
            record.IsAbsent = false;
            record.IsLate = Now > state.LateAfterAt;
        });

    public Task<AttendanceDetailModel> EndAssignmentAsync(int id, AttendanceMutationInput input) =>
        ChangeAttendanceAsync(id, input, "Ended", false, (c, record, state) =>
        {
            if (scope.Role != "Staff" || scope.UserId != c.Employee.UserId) throw new ForbiddenException("Only the assigned Staff can end this shift.");
            if (!state.CanEnd) throw new BadRequestException("This shift is not working or has already ended. Refresh the attendance details.");
            record.CheckOutAt = Now;
            return Task.CompletedTask;
        });

    public Task<AttendanceDetailModel> ReopenAssignmentAsync(int id, AttendanceMutationInput input) =>
        ChangeAttendanceAsync(id, input, "Reopened", true, async (c, record, state) =>
        {
            if (!state.CanReopen) throw new BadRequestException("Only an Absent shift that has not ended can be reopened. Use a time correction for a past shift.");
            if (await db.WorkOpenShifts.AnyAsync(o => o.OriginalAssignmentId == id && o.Status == "Filled"))
                throw new BadRequestException("This absence already has confirmed replacement coverage; resolve that coverage before reopening.");
            await EnsureNoOtherWorkingAsync(c.Employee.UserId, id);
            record.IsAbsent = false;
            record.CheckInAt = Now;
            record.CheckOutAt = null;
            record.IsLate = Now > state.LateAfterAt;
            record.AutoEnded = false;
        });

    public Task<AttendanceDetailModel> AdjustAttendanceAsync(int id, AttendanceAdjustmentInput input) =>
        ChangeAttendanceAsync(id, input, "Adjusted", true, async (c, record, state) =>
        {
            if (!input.CheckInAt.HasValue) throw new BadRequestException("Enter the actual start time.");
            var start = input.CheckInAt.Value.UtcDateTime;
            var end = input.CheckOutAt?.UtcDateTime;
            if (start > Now || end > Now || (end.HasValue && end < start)) throw new BadRequestException("Actual times cannot be in the future; the end must not precede the start.");
            if (!end.HasValue && Now > state.GraceEndsAt) throw new BadRequestException("Enter the actual end time when correcting a shift whose grace period has ended.");
            if (!end.HasValue) await EnsureNoOtherWorkingAsync(c.Employee.UserId, id);
            record.CheckInAt = start;
            record.CheckOutAt = end;
            record.IsAbsent = false;
            record.IsLate = start > state.LateAfterAt;
            record.AutoEnded = false;
        });

    private async Task<AttendanceDetailModel> ChangeAttendanceAsync(int id, AttendanceMutationInput input,
        string action, bool managerOnly, Func<AttendanceContext, DutyAttendance, AttendanceModel, Task> change)
    {
        var notices = new List<long>();
        var lotId = await WriteAsync(async () =>
        {
            var c = await LoadAttendanceContextAsync(id);
            await EnsureAttendanceAccessAsync(c, managerOnly);
            if (c.Roster.Status != "Published") throw new BadRequestException("Attendance is only recorded for published assignments.");
            CheckRevision(c.Roster.Revision, input.RosterRevision);
            var record = await db.DutyAttendances.SingleOrDefaultAsync(x => x.DutyAssignmentId == id)
                ?? new DutyAttendance { DutyAssignmentId = id };
            if (record.Revision != input.Revision) throw new ConflictException("Attendance changed. Refresh the details before submitting again.");
            var reason = input.Reason?.Trim();
            if (reason?.Length > 500 || (managerOnly && string.IsNullOrWhiteSpace(reason)))
                throw new BadRequestException("A reason of at most 500 characters is required for Manager attendance changes.");
            var before = DescribeAttendance(c.Assignment, c.Shift, c.Roster, c.StaffAvailable, record, Now);
            await change(c, record, before);
            if (action == "Reopened") notices.AddRange(await StopOffersForReopenAsync(id, reason));
            notices.AddRange(await SaveAttendanceEventAsync(c, record, before, action, scope.UserId, reason));
            return c.Roster.ParkingLotId;
        });
        QueueAttendancePush(lotId, notices);
        return await AttendanceDetailAsync(id);
    }

    private async Task EnsureNoOtherWorkingAsync(string userId, int excludeId)
    {
        var other = await (from at in db.DutyAttendances
            join a in db.DutyAssignments on at.DutyAssignmentId equals a.Id
            join s in db.DutyRosterShifts on a.DutyRosterShiftId equals s.Id
            join e in db.Employees on a.EmployeeId equals e.Id
            where e.UserId == userId && a.Id != excludeId && !a.IsCancelled && !s.IsCancelled
                && at.CheckInAt != null && at.CheckOutAt == null select new { s.EndsAt }).ToListAsync();
        if (other.Any(x => Now <= x.EndsAt.AddMinutes(attendanceRules.EndGraceMinutes)))
            throw new BadRequestException("This Staff member is still working another shift. End it first.");
    }

    private async Task<List<long>> SaveAttendanceEventAsync(AttendanceContext c, DutyAttendance record,
        AttendanceModel before, string action, string actorId, string reason)
    {
        if (record.Id == 0) db.DutyAttendances.Add(record);
        record.Revision++;
        var after = DescribeAttendance(c.Assignment, c.Shift, c.Roster, c.StaffAvailable, record, Now);
        db.DutyAttendanceEvents.Add(new DutyAttendanceEvent { DutyAssignmentId = c.Assignment.Id, Revision = record.Revision,
            Action = action, ActorId = actorId, Reason = reason, CreatedAt = Now,
            BeforeJson = JsonSerializer.Serialize(before), AfterJson = JsonSerializer.Serialize(after) });
        var recipients = new HashSet<string> { c.Employee.UserId };
        if (action is "Absent" or "AutoEnded" || (action == "Started" && record.IsLate))
        {
            var manager = await db.ParkingLots.Where(l => l.Id == c.Roster.ParkingLotId).Select(l => l.ManagerUserId).SingleAsync();
            if (!string.IsNullOrEmpty(manager)) recipients.Add(manager);
        }
        var notices = recipients.Select(userId => new UserNotification { UserId = userId, Type = "AttendanceChanged",
            Title = $"Attendance: {action}", Message = $"{c.Employee.FullName} · {c.Shift.Name} · {c.Shift.WorkDate:yyyy-MM-dd}. {reason}".Trim(),
            RefType = "DutyAssignment", RefId = $"{c.Assignment.Id}:{record.Revision}", CreatedAt = Now, EventAt = Now,
            DataJson = JsonSerializer.Serialize(new Dictionary<string, string> {
                ["assignmentId"] = c.Assignment.Id.ToString(CultureInfo.InvariantCulture),
                ["lotId"] = c.Roster.ParkingLotId.ToString(CultureInfo.InvariantCulture) }) }).ToList();
        db.UserNotifications.AddRange(notices);
        await SaveWorkChangesAsync();
        return notices.Select(n => n.Id).ToList();
    }

    private void QueueAttendancePush(int lotId, List<long> ids)
    {
        if (jobs == null) return;
        try { jobs.Enqueue<WorkAttendanceJobs>(j => j.PublishAsync(lotId, ids.ToArray())); }
        catch (Exception ex) { logger?.LogWarning(ex, "Attendance committed; clients can recover through refetching."); }
    }

    /// <summary>Hangfire timer; only real active published assignments enter this query, never empty cells.</summary>
    public async Task<int> UpdateDueAttendanceAsync()
    {
        var pending = new Dictionary<int, List<long>>();
        var count = await WriteAsync(async () =>
        {
            var now = Now;
            var absentCutoff = now.AddMinutes(-attendanceRules.AbsentAfterMinutes);
            var endCutoff = now.AddMinutes(-attendanceRules.EndGraceMinutes);
            var activeUsers = await identity.Users.Where(u => u.Role == "Staff" && u.IsActive).Select(u => u.Id).ToListAsync();
            var ids = await (from a in db.DutyAssignments
                join s in db.DutyRosterShifts on a.DutyRosterShiftId equals s.Id
                join r in db.DutyRosters on s.DutyRosterId equals r.Id
                join e in db.Employees on a.EmployeeId equals e.Id
                join l in db.ParkingLots on r.ParkingLotId equals l.Id
                join at in db.DutyAttendances on a.Id equals at.DutyAssignmentId into records
                from at in records.DefaultIfEmpty()
                let startReference = a.Source == "Replacement" && a.AttendanceTrackingFrom > s.StartsAt ? a.AttendanceTrackingFrom : s.StartsAt
                where !a.IsCancelled && !s.IsCancelled && !l.IsDeleted && r.Status == "Published"
                    && e.Status == "Active" && e.ParkingLotId == r.ParkingLotId && activeUsers.Contains(e.UserId)
                    && (at == null || (!at.IsAbsent && at.CheckOutAt == null))
                    && (((at == null || at.CheckInAt == null) && startReference < absentCutoff
                        && a.AttendanceTrackingFrom <= startReference.AddMinutes(attendanceRules.AbsentAfterMinutes)
                        && r.PublishedAt != null && r.PublishedAt <= startReference.AddMinutes(attendanceRules.AbsentAfterMinutes))
                        || (at != null && at.CheckInAt != null && s.EndsAt < endCutoff))
                orderby s.StartsAt select a.Id).Take(200).ToListAsync();
            foreach (var id in ids)
            {
                var c = await LoadAttendanceContextAsync(id);
                if (await IsAttendanceLockedAsync(c.Roster.ParkingLotId, c.Shift.StartsAt, c.Shift.EndsAt)) continue;
                var record = await db.DutyAttendances.SingleOrDefaultAsync(x => x.DutyAssignmentId == id)
                    ?? new DutyAttendance { DutyAssignmentId = id };
                if (record.CheckInAt.HasValue) {
                    var zone = RotationZone(c.Shift.TimeZoneId);
                    var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Utc(c.Shift.EndsAt.AddTicks(-1)), zone));
                    if (await IsAttendanceLockedAsync(c.Roster.ParkingLotId, c.Shift.StartsAt, DayBounds(WeekStart(last).AddDays(6), zone).End)) continue;
                }
                var before = DescribeAttendance(c.Assignment, c.Shift, c.Roster, c.StaffAvailable, record, now);
                var action = record.CheckInAt.HasValue ? "AutoEnded" : "Absent";
                if (action == "Absent") record.IsAbsent = true;
                else { record.CheckOutAt = record.CheckInAt > c.Shift.EndsAt ? record.CheckInAt : c.Shift.EndsAt; record.AutoEnded = true; }
                var notices = await SaveAttendanceEventAsync(c, record, before, action, "System",
                    action == "Absent" ? "Start deadline passed without a check-in." : "End grace period elapsed; scheduled end used for review.");
                if (action == "Absent" && now < c.Shift.EndsAt)
                    await NewOpenShiftAsync(c.Shift, "Uncovered absence after the start deadline.", c.Assignment.Id, null, null, notices);
                if (!pending.TryGetValue(c.Roster.ParkingLotId, out var list)) pending[c.Roster.ParkingLotId] = list = [];
                list.AddRange(notices);
            }
            return ids.Count;
        });
        foreach (var pair in pending) QueueAttendancePush(pair.Key, pair.Value);
        return count;
    }

    private async Task EnsureAttendanceScheduleEditableAsync(int assignmentId)
    {
        await EnsureNoPendingRequestAsync(assignmentId);
        var c = await LoadAttendanceContextAsync(assignmentId);
        if (await db.WorkOpenShifts.AnyAsync(o => o.ReplacementAssignmentId == assignmentId && o.Status == "Filled"))
            throw new BadRequestException("This is a confirmed replacement. Use its leave/swap workflow to preserve replacement history.");
        var exists = await db.DutyAttendances.AnyAsync(x => x.DutyAssignmentId == assignmentId);
        if (exists || (c.Roster.Status == "Published" && c.Roster.PublishedAt.HasValue && c.Roster.PublishedAt <= c.Shift.StartsAt.AddMinutes(attendanceRules.AbsentAfterMinutes)
            && c.Assignment.AttendanceTrackingFrom <= c.Shift.StartsAt.AddMinutes(attendanceRules.AbsentAfterMinutes)
            && Now > c.Shift.StartsAt.AddMinutes(attendanceRules.AbsentAfterMinutes)))
            throw new BadRequestException("This assignment has attendance history or a passed start deadline. Correct attendance instead of removing, replacing or moving it.");
    }
}
