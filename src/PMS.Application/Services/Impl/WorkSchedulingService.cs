using System.Data;
using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.Application.Jobs;
using PMS.Application.Models.WorkScheduling;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

public partial class WorkSchedulingService(PmsDbContext db, DatabaseContext identity, LotScopeService scope,
    TimeProvider clock, IBackgroundJobClient jobs = null, ILogger<WorkSchedulingService> logger = null,
    WorkAttendanceOptions attendanceOptions = null, WorkRotationRules rotationRules = null, IApprovedWorkLeaveSource approvedLeaveSource = null,
    WorkRequestOptions requestOptions = null)
{
    private static readonly string[] Positions = ["Entry", "Exit", "Counter"];
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private async Task EnsureManagerAsync(int lotId)
    {
        // Admin is deliberately not a roster editor (SM role matrix).
        if (scope.Role != "Manager") throw new ForbiddenException("Only the lot's Manager manages work schedules.");
        await scope.EnsureCanManageAsync(lotId);
    }

    public async Task<IReadOnlyList<WorkShiftTemplate>> TemplatesAsync(int lotId)
    {
        await EnsureManagerAsync(lotId);
        return await db.WorkShiftTemplates.AsNoTracking().Where(t => t.ParkingLotId == lotId).OrderBy(t => t.Name).ToListAsync();
    }

    public async Task<WorkShiftTemplate> SaveTemplateAsync(int lotId, int? id, WorkShiftTemplateInput input)
    {
        await EnsureManagerAsync(lotId);
        return await WriteAsync(async () =>
        {
            var name = Name(input.Name);
            if (input.StartTime == input.EndTime) throw new BadRequestException("Start and end times must differ; a shift must be shorter than 24 hours.");
            if (new[] { input.MinimumEntryStaff, input.MinimumExitStaff, input.MinimumCounterStaff }.Any(n => n < 0 || n > 100))
                throw new BadRequestException("Minimum staffing must be between 0 and 100 per position.");
            if (await db.WorkShiftTemplates.AnyAsync(t => t.ParkingLotId == lotId && t.Id != id && t.Name == name))
                throw new BadRequestException("A shift template with this name already exists at this lot.");
            var t = id.HasValue
                ? await db.WorkShiftTemplates.SingleOrDefaultAsync(t => t.Id == id && t.ParkingLotId == lotId)
                    ?? throw new NotFoundException("Shift template not found.")
                : new WorkShiftTemplate { ParkingLotId = lotId };
            if (id.HasValue) { CheckRevision(t.Revision, input.Revision); t.Revision++; }
            else db.WorkShiftTemplates.Add(t);
            t.Name = name;
            t.StartTime = input.StartTime;
            t.EndTime = input.EndTime;
            t.MinimumEntryStaff = input.MinimumEntryStaff;
            t.MinimumExitStaff = input.MinimumExitStaff;
            t.MinimumCounterStaff = input.MinimumCounterStaff;
            t.IsActive = input.IsActive;
            await SaveWorkChangesAsync();
            return t;
        });
    }

    public async Task<IReadOnlyList<RosterStaffModel>> StaffAsync(int lotId)
    {
        await EnsureManagerAsync(lotId);
        return await AvailableSchedulingStaffAsync(lotId);
    }

    private async Task<IReadOnlyList<RosterStaffModel>> AvailableSchedulingStaffAsync(int lotId)
    {
        var activeIds = await identity.Users.Where(u => u.Role == "Staff" && u.IsActive).Select(u => u.Id).ToListAsync();
        return await db.Employees.AsNoTracking().Where(e => e.ParkingLotId == lotId && e.Status == "Active" && activeIds.Contains(e.UserId))
            .OrderBy(e => e.FullName).Select(e => new RosterStaffModel(e.Id, e.UserId, e.FullName, e.EmployeeCode)).ToListAsync();
    }

    public async Task<IReadOnlyList<DutyRosterModel>> RostersAsync(int lotId)
    {
        await EnsureManagerAsync(lotId);
        var rosters = await db.DutyRosters.AsNoTracking().Where(r => r.ParkingLotId == lotId).OrderByDescending(r => r.FromDate).Take(100).ToListAsync();
        var result = new List<DutyRosterModel>();
        foreach (var r in rosters) result.Add(await ModelAsync(r));
        return result;
    }

    public async Task<DutyRosterModel> CreateRosterAsync(int lotId, CreateDutyRosterInput input)
    {
        await EnsureManagerAsync(lotId);
        if (input.FromDate == default || input.ToDate < input.FromDate || input.ToDate.DayNumber - input.FromDate.DayNumber > 92)
            throw new BadRequestException("Choose a valid roster period of at most 93 days.");
        return await WriteAsync(async () =>
        {
            var r = new DutyRoster { ParkingLotId = lotId, Name = Name(input.Name), FromDate = input.FromDate,
                ToDate = input.ToDate, CreatedBy = scope.UserId, CreatedAt = Now };
            db.DutyRosters.Add(r);
            await SaveWorkChangesAsync();
            var model = await ModelAsync(r);
            Log(r, "Created", null, model, null);
            await SaveWorkChangesAsync();
            return model;
        });
    }

    public Task<DutyRosterModel> SaveShiftAsync(int lotId, int rosterId, int? shiftId, RosterShiftInput input) =>
        MutateAsync(lotId, rosterId, input, shiftId.HasValue ? "ShiftUpdated" : "ShiftAdded", async r =>
        {
            if (input.WorkDate < r.FromDate || input.WorkDate > r.ToDate)
                throw new BadRequestException("The shift's start date must fall within the roster period.");
            if (await db.DutyRosterShifts.AnyAsync(s => s.DutyRosterId == r.Id && !s.IsCancelled && s.Id != shiftId
                && s.WorkDate == input.WorkDate && s.WorkShiftTemplateId == input.WorkShiftTemplateId))
                throw new BadRequestException("This template already has a shift on this date in the roster.");
            var s = await TemplateSnapshotAsync(lotId, r.Id, input.WorkDate, input.WorkShiftTemplateId);
            if (shiftId.HasValue)
            {
                var tracked = await FindShiftAsync(r.Id, shiftId.Value);
                if (await db.DutyAssignments.AnyAsync(a => a.DutyRosterShiftId == tracked.Id && a.CancellationKind != null))
                    throw new BadRequestException("This occurrence retains approved leave or swap history; its original shift snapshot must be preserved.");
                if (await db.WorkOpenShifts.AnyAsync(o => o.DutyRosterShiftId == tracked.Id && o.Status == "Open"))
                    throw new BadRequestException("Close the open replacement offers before editing this dated shift.");
                if (tracked.StartsAt != s.StartsAt || tracked.EndsAt != s.EndsAt)
                {
                    foreach (var assignmentId in await db.DutyAssignments.Where(a => a.DutyRosterShiftId == tracked.Id && !a.IsCancelled).Select(a => a.Id).ToListAsync())
                        await EnsureAttendanceScheduleEditableAsync(assignmentId);
                    foreach (var assignment in await db.DutyAssignments.Where(a => a.DutyRosterShiftId == tracked.Id && !a.IsCancelled).ToListAsync())
                        assignment.AttendanceTrackingFrom = Now;
                }
                s.Id = tracked.Id;
                foreach (var assignment in await db.DutyAssignments.Where(a => a.DutyRosterShiftId == tracked.Id && !a.IsCancelled).ToListAsync())
                    assignment.IsManuallyEdited = true;
                db.Entry(tracked).CurrentValues.SetValues(s);
            }
            else db.DutyRosterShifts.Add(s);
            foreach (var a in await db.DutyAssignments.Where(a => a.DutyRosterShiftId == s.Id && !a.IsCancelled).ToListAsync())
                await ValidateAssignmentAsync(lotId, s, a.EmployeeId, a.Position, a.Id);
        });

    public Task<DutyRosterModel> RemoveShiftAsync(int lotId, int rosterId, int shiftId, RosterMutationInput input) =>
        MutateAsync(lotId, rosterId, input, "ShiftRemoved", async r =>
        {
            var s = await FindShiftAsync(r.Id, shiftId);
            if (await db.DutyAssignments.AnyAsync(a => a.DutyRosterShiftId == s.Id && a.CancellationKind != null))
                throw new BadRequestException("This occurrence retains approved leave or swap history and cannot be removed.");
            if (await db.WorkOpenShifts.AnyAsync(o => o.DutyRosterShiftId == s.Id && o.Status == "Open")
                || await db.WorkManagerCoverages.AnyAsync(c => c.DutyRosterShiftId == s.Id))
                throw new BadRequestException("Resolve open offers or recorded Manager coverage before removing this shift.");
            foreach (var assignmentId in await db.DutyAssignments.Where(a => a.DutyRosterShiftId == s.Id && !a.IsCancelled).Select(a => a.Id).ToListAsync())
                await EnsureAttendanceScheduleEditableAsync(assignmentId);
            s.IsCancelled = true;
            foreach (var a in await db.DutyAssignments.Where(a => a.DutyRosterShiftId == s.Id && !a.IsCancelled).ToListAsync()) { a.IsCancelled = true; a.IsManuallyEdited = true; }
        });

    public Task<DutyRosterModel> SaveAssignmentAsync(int lotId, int rosterId, int shiftId, int? assignmentId, DutyAssignmentInput input) =>
        MutateAsync(lotId, rosterId, input, assignmentId.HasValue ? "AssignmentUpdated" : "AssignmentAdded", async r =>
        {
            var s = await FindShiftAsync(r.Id, shiftId);
            var a = assignmentId.HasValue
                ? await db.DutyAssignments.SingleOrDefaultAsync(a => a.Id == assignmentId && a.DutyRosterShiftId == s.Id && !a.IsCancelled)
                    ?? throw new NotFoundException("Assignment not found.")
                : new DutyAssignment { DutyRosterShiftId = s.Id, AttendanceTrackingFrom = Now };
            if (input.TargetShiftId.HasValue) s = await FindShiftAsync(r.Id, input.TargetShiftId.Value);
            if (!assignmentId.HasValue || a.EmployeeId != input.EmployeeId || a.DutyRosterShiftId != s.Id || a.Position != input.Position)
                await EnsureNoOpenSlotBypassAsync(r, s.Id, input.Position);
            if (assignmentId.HasValue && (a.EmployeeId != input.EmployeeId || a.DutyRosterShiftId != s.Id))
            {
                await EnsureAttendanceScheduleEditableAsync(a.Id);
                a.AttendanceTrackingFrom = Now;
            }
            await ValidateAssignmentAsync(lotId, s, input.EmployeeId, input.Position, assignmentId);
            if (assignmentId.HasValue && a.Position != input.Position) await EnsureNoPendingRequestAsync(a.Id);
            if (assignmentId.HasValue && a.Position != input.Position && a.Source == "Replacement") await EnsureAttendanceScheduleEditableAsync(a.Id);
            if (assignmentId.HasValue) a.IsManuallyEdited = true;
            a.DutyRosterShiftId = s.Id;
            a.EmployeeId = input.EmployeeId;
            a.Position = input.Position;
            a.Notes = Notes(input.Notes);
            if (!assignmentId.HasValue) db.DutyAssignments.Add(a);
        });

    /// <summary>One calendar-cell action, one roster revision, one transaction and one history entry.</summary>
    public Task<DutyRosterModel> AssignFromCalendarAsync(int lotId, int rosterId, CalendarAssignmentInput input) =>
        MutateAsync(lotId, rosterId, input, "AssignmentAdded", async r =>
        {
            var notes = Notes(input.Notes);
            var s = await CalendarShiftAsync(lotId, r, input.ShiftId, input.WorkDate, input.WorkShiftTemplateId);
            await EnsureNoOpenSlotBypassAsync(r, s.Id, input.Position);
            await ValidateAssignmentAsync(lotId, s, input.EmployeeId, input.Position, null);
            if (s.Id == 0)
            {
                db.DutyRosterShifts.Add(s);
                await SaveWorkChangesAsync();
            }
            db.DutyAssignments.Add(new DutyAssignment { DutyRosterShiftId = s.Id, EmployeeId = input.EmployeeId,
                Position = input.Position, Notes = notes, AttendanceTrackingFrom = Now });
        });

    public async Task<IReadOnlyList<RosterStaffModel>> EligibleStaffAsync(int lotId, int rosterId,
        int? shiftId, DateOnly workDate, int? templateId, int? excludeAssignmentId)
    {
        await EnsureManagerAsync(lotId);
        var r = await FindRosterAsync(lotId, rosterId);
        if (excludeAssignmentId.HasValue && !await (from a in db.DutyAssignments
            join s in db.DutyRosterShifts on a.DutyRosterShiftId equals s.Id
            where a.Id == excludeAssignmentId && !a.IsCancelled && !s.IsCancelled && s.DutyRosterId == r.Id select a.Id).AnyAsync())
            throw new NotFoundException("Assignment not found in this roster.");
        var target = await CalendarShiftAsync(lotId, r, shiftId, workDate, templateId);
        var busy = await (from a in db.DutyAssignments join s in db.DutyRosterShifts on a.DutyRosterShiftId equals s.Id
            join e in db.Employees on a.EmployeeId equals e.Id
            where !a.IsCancelled && !s.IsCancelled && a.Id != excludeAssignmentId
                && s.StartsAt < target.EndsAt && target.StartsAt < s.EndsAt select e.UserId).Distinct().ToListAsync();
        return (await StaffAsync(lotId)).Where(e => !busy.Contains(e.UserId)).ToList();
    }

    private async Task<DutyRosterShift> CalendarShiftAsync(int lotId, DutyRoster r, int? shiftId, DateOnly date, int? templateId)
    {
        if (shiftId.HasValue) return await FindShiftAsync(r.Id, shiftId.Value);
        if (!templateId.HasValue || date < r.FromDate || date > r.ToDate)
            throw new BadRequestException("Choose a shift template and a start date within this roster.");
        // Reuse an already-created occurrence; never silently refresh its published snapshot.
        return await db.DutyRosterShifts.SingleOrDefaultAsync(s => s.DutyRosterId == r.Id && !s.IsCancelled
            && s.WorkDate == date && s.WorkShiftTemplateId == templateId)
            ?? await TemplateSnapshotAsync(lotId, r.Id, date, templateId.Value);
    }

    private async Task<DutyRosterShift> TemplateSnapshotAsync(int lotId, int rosterId, DateOnly date, int templateId)
    {
        var t = await db.WorkShiftTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == templateId && t.ParkingLotId == lotId && t.IsActive)
            ?? throw new BadRequestException("Choose an active shift template belonging to this lot.");
        var zoneId = await db.ParkingLots.Where(l => l.Id == lotId).Select(l => l.TimeZoneId).SingleAsync();
        var zone = ParkingText.FindTimeZone(zoneId);
        var localStart = date.ToDateTime(t.StartTime, DateTimeKind.Unspecified);
        var localEnd = date.ToDateTime(t.EndTime, DateTimeKind.Unspecified);
        if (t.EndTime < t.StartTime)
        {
            if (date == DateOnly.MaxValue) throw new BadRequestException("The overnight shift's end date is out of range.");
            localEnd = localEnd.AddDays(1);
        }
        if (zone.IsInvalidTime(localStart) || zone.IsInvalidTime(localEnd) || zone.IsAmbiguousTime(localStart) || zone.IsAmbiguousTime(localEnd))
            throw new BadRequestException("This shift falls in a daylight-saving time change; choose different hours.");
        return new DutyRosterShift { DutyRosterId = rosterId, WorkShiftTemplateId = t.Id, WorkDate = date,
            Name = t.Name, TimeZoneId = zone.Id, StartTime = t.StartTime, EndTime = t.EndTime,
            StartsAt = TimeZoneInfo.ConvertTimeToUtc(localStart, zone), EndsAt = TimeZoneInfo.ConvertTimeToUtc(localEnd, zone),
            MinimumEntryStaff = t.MinimumEntryStaff, MinimumExitStaff = t.MinimumExitStaff, MinimumCounterStaff = t.MinimumCounterStaff };
    }

    private static string Notes(string notes) => notes?.Trim().Length > 1000
        ? throw new BadRequestException("Assignment notes must be at most 1000 characters.")
        : string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

    public Task<DutyRosterModel> RemoveAssignmentAsync(int lotId, int rosterId, int shiftId, int assignmentId, RosterMutationInput input) =>
        MutateAsync(lotId, rosterId, input, "AssignmentRemoved", async r =>
        {
            await FindShiftAsync(r.Id, shiftId);
            var a = await db.DutyAssignments.SingleOrDefaultAsync(a => a.Id == assignmentId && a.DutyRosterShiftId == shiftId && !a.IsCancelled)
                ?? throw new NotFoundException("Assignment not found.");
            await EnsureAttendanceScheduleEditableAsync(a.Id);
            a.IsCancelled = true;
            a.IsManuallyEdited = true;
        });

    public Task<DutyRosterModel> PublishAsync(int lotId, int rosterId, RosterMutationInput input) =>
        MutateAsync(lotId, rosterId, input, "Published", async r =>
        {
            if (r.Status != "Draft") throw new BadRequestException("This roster is already published.");
            if (r.HasRotation)
            {
                var issues = await ValidateRotationRosterAsync(r);
                if (issues.Any(x => x.Blocking)) throw new BadRequestException(string.Join(" ", issues.Where(x => x.Blocking).Take(5).Select(x => x.Message)));
            }
            var shifts = await db.DutyRosterShifts.Where(s => s.DutyRosterId == r.Id && !s.IsCancelled).ToListAsync();
            if (shifts.Count == 0) throw new BadRequestException("Add at least one dated shift before publishing.");
            foreach (var s in shifts)
                foreach (var a in await db.DutyAssignments.Where(a => a.DutyRosterShiftId == s.Id && !a.IsCancelled).ToListAsync())
                    await ValidateAssignmentAsync(lotId, s, a.EmployeeId, a.Position, a.Id);
            r.Status = "Published";
            r.PublishedAt = Now;
        });

    public async Task<IReadOnlyList<RosterChangeModel>> HistoryAsync(int lotId, int rosterId)
    {
        await EnsureManagerAsync(lotId);
        await FindRosterAsync(lotId, rosterId);
        var history = await db.DutyRosterChangeLogs.AsNoTracking().Where(h => h.DutyRosterId == rosterId).OrderByDescending(h => h.Revision).ToListAsync();
        var actorIds = history.Select(h => h.ChangedBy).Distinct().ToList();
        var actors = await db.Employees.AsNoTracking().Where(e => actorIds.Contains(e.UserId)).Select(e => new { e.UserId, e.FullName }).ToListAsync();
        return history.Select(h => new RosterChangeModel(h.Id, h.DutyRosterId, h.Revision, h.Action, h.ChangedBy,
            actors.FirstOrDefault(e => e.UserId == h.ChangedBy)?.FullName ?? "Manager", Utc(h.ChangedAt), h.Reason, h.BeforeJson, h.AfterJson)).ToList();
    }

    public async Task<IReadOnlyList<DutyRosterModel>> MyScheduleAsync(DateOnly from, DateOnly to)
    {
        if (scope.Role != "Staff") throw new ForbiddenException("Only Staff can view their personal schedule.");
        if (from == default || to < from || to.DayNumber - from.DayNumber > 92) throw new BadRequestException("Choose a valid period of at most 93 days.");
        var employeeIds = await db.Employees.Where(e => e.UserId == scope.UserId && e.Status == "Active")
            .Select(e => e.Id).ToListAsync();
        var earliestDate = from.AddDays(-1);
        var rosterIds = await (from a in db.DutyAssignments
                               join s in db.DutyRosterShifts on a.DutyRosterShiftId equals s.Id
                               join r in db.DutyRosters on s.DutyRosterId equals r.Id
                               join e in db.Employees on a.EmployeeId equals e.Id
                               join l in db.ParkingLots on r.ParkingLotId equals l.Id
                               where employeeIds.Contains(a.EmployeeId) && !a.IsCancelled && !s.IsCancelled
                                  && r.Status == "Published" && !l.IsDeleted && e.ParkingLotId == r.ParkingLotId
                                  && s.WorkDate >= earliestDate && s.WorkDate <= to
                               select r.Id).Distinct().ToListAsync();
        rosterIds.AddRange(await (from s in db.DutyStandbyAssignments join r in db.DutyRosters on s.DutyRosterId equals r.Id
            join e in db.Employees on s.EmployeeId equals e.Id join l in db.ParkingLots on r.ParkingLotId equals l.Id
            where employeeIds.Contains(s.EmployeeId) && !s.IsCancelled && r.Status == "Published" && !l.IsDeleted
                && e.ParkingLotId == r.ParkingLotId && s.WorkDate >= @from && s.WorkDate <= to select r.Id).Distinct().ToListAsync());
        rosterIds.AddRange(await (from a in db.DutyAssignments join s in db.DutyRosterShifts on a.DutyRosterShiftId equals s.Id
            join r in db.DutyRosters on s.DutyRosterId equals r.Id join e in db.Employees on a.EmployeeId equals e.Id
            where employeeIds.Contains(e.Id) && e.ParkingLotId == r.ParkingLotId && r.Status == "Published" && !s.IsCancelled
                && a.CancellationKind != null && s.WorkDate >= earliestDate && s.WorkDate <= to select r.Id).Distinct().ToListAsync());
        var visibleSlotIds = new HashSet<int>();
        foreach (var employee in await db.Employees.Where(e => employeeIds.Contains(e.Id) && e.ParkingLotId != null).ToListAsync())
            visibleSlotIds.UnionWith(await VisibleSlotIdsAsync(employee.ParkingLotId.Value, employee.Id));
        rosterIds.AddRange(await (from o in db.WorkOpenShifts join s in db.DutyRosterShifts on o.DutyRosterShiftId equals s.Id
            where visibleSlotIds.Contains(o.Id) && s.WorkDate >= earliestDate && s.WorkDate <= to select s.DutyRosterId).Distinct().ToListAsync());
        var result = new List<DutyRosterModel>();
        foreach (var r in await db.DutyRosters.AsNoTracking().Where(r => rosterIds.Contains(r.Id)).ToListAsync())
        {
            var model = await ModelAsync(r);
            // Include an overnight shift from yesterday only if it actually extends into the requested dates.
            var shifts = model.Shifts.Where(s => s.WorkDate <= to && (s.WorkDate >= from || (s.EndTime < s.StartTime && s.EndTime != TimeOnly.MinValue)))
                .Select(s => s with { Assignments = s.Assignments.Where(a => employeeIds.Contains(a.EmployeeId)).ToList(),
                    Trail = s.Trail.Where(t => employeeIds.Contains(t.EmployeeId)).ToList(),
                    OpenShifts = s.OpenShifts.Where(o => visibleSlotIds.Contains(o.Id)).ToList(), ManagerCoverage = [] })
                .Where(s => (s.Assignments.Count > 0 || s.Trail.Count > 0 || s.OpenShifts.Count > 0) && s.WorkDate >= from.AddDays(-1)).ToList();
            var standbys = model.Standbys.Where(s => employeeIds.Contains(s.EmployeeId) && s.WorkDate >= from && s.WorkDate <= to).ToList();
            if (shifts.Count > 0 || standbys.Count > 0) result.Add(model with { Shifts = shifts, Shortages = [], Standbys = standbys, RotationIssues = [], RotationConfigurationJson = null });
        }
        return result.OrderBy(r => r.FromDate).ToList();
    }

    private async Task<DutyRosterModel> MutateAsync(int lotId, int rosterId, RosterMutationInput input, string action, Func<DutyRoster, Task> mutate)
    {
        await EnsureManagerAsync(lotId);
        var noticeIds = new List<long>();
        var result = await WriteAsync(async () =>
        {
            var r = await FindRosterAsync(lotId, rosterId);
            CheckRevision(r.Revision, input.Revision);
            var reason = input.Reason?.Trim();
            if (reason?.Length > 500) throw new BadRequestException("Reason must be at most 500 characters.");
            if (r.Status == "Published" && string.IsNullOrEmpty(reason)) throw new BadRequestException("Give a reason when changing a published roster.");
            var before = await ModelAsync(r);
            await mutate(r);
            r.Revision++;
            await SaveWorkChangesAsync();
            var after = await ModelAsync(r);
            Log(r, action, before, after, reason);
            var notices = new List<UserNotification>();
            if (r.Status == "Published")
            {
                var oldSignatures = StaffSignatures(before);
                var newSignatures = StaffSignatures(after);
                var affected = oldSignatures.Keys.Union(newSignatures.Keys).Where(id => before.Status != "Published"
                    || oldSignatures.GetValueOrDefault(id) != newSignatures.GetValueOrDefault(id)).ToList();
                var users = await db.Employees.Where(e => affected.Contains(e.Id)).Select(e => e.UserId).Distinct().ToListAsync();
                foreach (var userId in users)
                {
                    var notice = new UserNotification { UserId = userId, Type = action == "Published" ? "RosterPublished" : "RosterChanged",
                        Title = action == "Published" ? "Work schedule published" : "Your work schedule changed",
                        Message = $"{r.Name}: {(action == "Published" ? "your schedule is now available." : reason)}",
                        RefType = "DutyRoster", RefId = $"{r.Id}:{r.Revision}", EventAt = Now, CreatedAt = Now,
                        DataJson = JsonSerializer.Serialize(new Dictionary<string, string>
                        {
                            ["rosterId"] = r.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["lotId"] = lotId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["revision"] = r.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        }) };
                    notices.Add(notice);
                    db.UserNotifications.Add(notice);
                }
            }
            await SaveWorkChangesAsync();
            noticeIds.AddRange(notices.Select(n => n.Id));
            return after;
        });
        if (noticeIds.Count > 0 && jobs != null)
        {
            try { jobs.Enqueue<WorkScheduleNotificationJob>(j => j.PushAsync(noticeIds.ToArray())); }
            catch (Exception ex) { logger?.LogWarning(ex, "Roster saved; durable notifications remain available through the notification bell."); }
        }
        return result;
    }

    private async Task ValidateAssignmentAsync(int lotId, DutyRosterShift shift, int employeeId, string position, int? excludeId)
    {
        if (!Positions.Contains(position)) throw new BadRequestException("Position must be Entry, Exit or Counter.");
        var e = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == employeeId && e.ParkingLotId == lotId && e.Status == "Active")
            ?? throw new BadRequestException("Staff must be active and assigned to this lot.");
        if (!await identity.Users.AnyAsync(u => u.Id == e.UserId && u.Role == "Staff" && u.IsActive))
            throw new BadRequestException("Choose an active Staff account.");
        if ((await LeaveSource.ReadAsync(lotId, shift.StartsAt, shift.EndsAt)).Leaves.Any(l => l.EmployeeId == employeeId && l.StartsAt < shift.EndsAt && shift.StartsAt < l.EndsAt))
            throw new BadRequestException("Approved leave overlaps this assignment.");
        // Compare actual instants across ALL rosters and adjacent work dates; touching boundaries are allowed.
        if (await (from a in db.DutyAssignments join s in db.DutyRosterShifts on a.DutyRosterShiftId equals s.Id
                   join other in db.Employees on a.EmployeeId equals other.Id
                   where other.UserId == e.UserId && !a.IsCancelled && !s.IsCancelled && a.Id != excludeId
                       && s.StartsAt < shift.EndsAt && shift.StartsAt < s.EndsAt
                   select a.Id).AnyAsync())
            throw new BadRequestException($"{e.FullName} already has an overlapping assignment (including draft schedules).");
    }

    private async Task<DutyRoster> FindRosterAsync(int lotId, int id) =>
        await db.DutyRosters.SingleOrDefaultAsync(r => r.Id == id && r.ParkingLotId == lotId)
        ?? throw new NotFoundException("Roster not found.");

    private async Task<DutyRosterShift> FindShiftAsync(int rosterId, int shiftId) =>
        await db.DutyRosterShifts.SingleOrDefaultAsync(s => s.Id == shiftId && s.DutyRosterId == rosterId && !s.IsCancelled)
        ?? throw new NotFoundException("Dated shift not found.");

    private async Task<DutyRosterModel> ModelAsync(DutyRoster r)
    {
        var shifts = await db.DutyRosterShifts.AsNoTracking().Where(s => s.DutyRosterId == r.Id && !s.IsCancelled).OrderBy(s => s.StartsAt).ThenBy(s => s.Id).ToListAsync();
        var ids = shifts.Select(s => s.Id).ToList();
        var assignments = await (from a in db.DutyAssignments.AsNoTracking() join e in db.Employees on a.EmployeeId equals e.Id
                                 where ids.Contains(a.DutyRosterShiftId) && !a.IsCancelled
                                 select new { a.DutyRosterShiftId, Assignment = a, e.UserId,
                                     Available = e.Status == "Active" && e.ParkingLotId == r.ParkingLotId,
                                     Model = new AssignmentModel(a.Id, e.Id, e.FullName, a.Position, a.Notes) { Source = a.Source, IsManuallyEdited = a.IsManuallyEdited } }).ToListAsync();
        var assignmentIds = assignments.Select(a => a.Assignment.Id).ToList();
        var attendance = await db.DutyAttendances.AsNoTracking().Where(a => assignmentIds.Contains(a.DutyAssignmentId)).ToDictionaryAsync(a => a.DutyAssignmentId);
        var userIds = assignments.Select(a => a.UserId).Distinct().ToList();
        var activeUsers = await identity.Users.Where(u => userIds.Contains(u.Id) && u.IsActive && u.Role == "Staff").Select(u => u.Id).ToListAsync();
        var now = Now;
        var lockedPeriods = await db.AttendancePeriodLots.AsNoTracking().Where(p => p.ParkingLotId == r.ParkingLotId && p.Status == "Locked").ToListAsync();
        var models = shifts.Select(s => new RosterShiftModel(s.Id, s.WorkShiftTemplateId, s.WorkDate, s.Name, s.TimeZoneId,
            s.StartTime, s.EndTime, Utc(s.StartsAt), Utc(s.EndsAt), s.MinimumEntryStaff, s.MinimumExitStaff, s.MinimumCounterStaff,
            assignments.Where(a => a.DutyRosterShiftId == s.Id).Select(a => a.Model with {
                Attendance = DescribeAttendance(a.Assignment, s, r, a.Available && activeUsers.Contains(a.UserId), attendance.GetValueOrDefault(a.Assignment.Id), now)
            }).ToList())
            {
                AttendanceLocked = lockedPeriods.Any(p => s.StartsAt < p.EndsAt && p.StartsAt < s.EndsAt),
                EntryAssigned = assignments.Count(a => a.DutyRosterShiftId == s.Id && a.Model.Position == "Entry" && DescribeAttendance(a.Assignment, s, r, a.Available && activeUsers.Contains(a.UserId), attendance.GetValueOrDefault(a.Assignment.Id), now).Status != "Absent"),
                ExitAssigned = assignments.Count(a => a.DutyRosterShiftId == s.Id && a.Model.Position == "Exit" && DescribeAttendance(a.Assignment, s, r, a.Available && activeUsers.Contains(a.UserId), attendance.GetValueOrDefault(a.Assignment.Id), now).Status != "Absent"),
                CounterAssigned = assignments.Count(a => a.DutyRosterShiftId == s.Id && a.Model.Position == "Counter" && DescribeAttendance(a.Assignment, s, r, a.Available && activeUsers.Contains(a.UserId), attendance.GetValueOrDefault(a.Assignment.Id), now).Status != "Absent")
            }).ToList();
        var shortages = models.SelectMany(s => Positions.Select((p, i) => new ShortageModel(s.Id, s.WorkDate, s.Name, p,
            new[] { s.MinimumEntryStaff, s.MinimumExitStaff, s.MinimumCounterStaff }[i], s.Assignments.Count(a => a.Position == p))))
            .Where(x => x.Assigned < x.Required).ToList();
        var model = new DutyRosterModel(r.Id, r.ParkingLotId, r.Name, r.FromDate, r.ToDate, r.Status, r.Revision,
            r.PublishedAt.HasValue ? Utc(r.PublishedAt.Value) : null, models, shortages) {
                AttendanceLockedDates = (await (from p in db.AttendancePeriods join l in db.AttendancePeriodLots on p.Id equals l.AttendancePeriodId
                    where l.ParkingLotId == r.ParkingLotId && l.Status == "Locked" && p.FromDate <= r.ToDate && r.FromDate <= p.ToDate
                    select new { p.FromDate, p.ToDate }).ToListAsync())
                    .SelectMany(p => Enumerable.Range(Math.Max(r.FromDate.DayNumber, p.FromDate.DayNumber),
                        Math.Min(r.ToDate.DayNumber, p.ToDate.DayNumber) - Math.Max(r.FromDate.DayNumber, p.FromDate.DayNumber) + 1).Select(DateOnly.FromDayNumber)).Distinct().OrderBy(x => x).ToList(),
                Standbys = await StandbysAsync(r.Id),
                RotationConfigurationJson = r.RotationConfigurationJson,
                RotationIssues = r.HasRotation ? await ValidateRotationRosterAsync(r) : []
            };
        return await DecorateRequestModelsAsync(model);
    }

    private void Log(DutyRoster r, string action, DutyRosterModel before, DutyRosterModel after, string reason) =>
        db.DutyRosterChangeLogs.Add(new DutyRosterChangeLog { DutyRosterId = r.Id, Revision = r.Revision, Action = action,
            ChangedBy = scope.UserId, ChangedAt = Now, Reason = reason,
            BeforeJson = before == null ? null : JsonSerializer.Serialize(before), AfterJson = JsonSerializer.Serialize(after) });

    private static Dictionary<int, string> StaffSignatures(DutyRosterModel r) => r.Shifts
        .SelectMany(s => s.Assignments.Select(a => new { a.EmployeeId, Signature = JsonSerializer.Serialize(new { s.Id, s.Name, s.StartsAt, s.EndsAt, a.Position, a.Notes }) }))
        .Concat(r.Standbys.Select(s => new { s.EmployeeId, Signature = $"Standby:{s.Id}:{s.WorkDate:yyyy-MM-dd}" }))
        .GroupBy(x => x.EmployeeId).ToDictionary(g => g.Key, g => string.Join(";", g.Select(x => x.Signature).OrderBy(x => x)));

    private async Task<T> WriteAsync<T>(Func<Task<T>> operation)
    {
        // This transaction belongs to PmsDbContext, independently of the identity middleware transaction.
        // A transaction-scoped SQL application lock serializes schedule writers across API processes.
        // This prevents two concurrent overlap checks from both succeeding on empty ranges.
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable) : null;
        if (db.Database.IsSqlServer())
            await db.Database.ExecuteSqlRawAsync("""
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource = N'PMS.WorkScheduling', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
                IF @result < 0 THROW 51000, 'Work schedule is busy; please retry.', 1;
                """);
        try
        {
            var result = await operation();
            if (tx != null) await tx.CommitAsync();
            return result;
        }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("This schedule changed. Refresh it before saving again."); }
    }

    private static void CheckRevision(int current, int expected)
    {
        if (current != expected) throw new ConflictException("This schedule changed. Refresh it before saving again.");
    }

    private static string Name(string name) => string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100
        ? throw new BadRequestException("Enter a name of at most 100 characters.") : name.Trim();
    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
}
