using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.WorkScheduling;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

public partial class WorkSchedulingService
{
    private WorkRotationRules RotationRules => rotationRules ?? new WorkRotationRules();
    private IApprovedWorkLeaveSource LeaveSource => approvedLeaveSource ?? new ApprovedWorkLeaveSource(db);

    public async Task<IReadOnlyList<RotationGroupModel>> RotationGroupsAsync(int lotId)
    {
        await EnsureManagerAsync(lotId);
        var groups = await db.WorkRotationGroups.AsNoTracking().Where(g => g.ParkingLotId == lotId).OrderBy(g => g.Id).ToListAsync();
        var ids = groups.Select(g => g.Id).ToList();
        var members = await db.WorkRotationMembers.AsNoTracking().Where(m => ids.Contains(m.WorkRotationGroupId)).OrderBy(m => m.EmployeeId).ToListAsync();
        return groups.Select(g => new RotationGroupModel(g.Id, g.Name, g.OffsetDays, g.IsActive, g.Revision,
            members.Where(m => m.WorkRotationGroupId == g.Id).Select(m => new RotationMemberInput(m.EmployeeId)).ToList())).ToList();
    }

    public async Task<RotationGroupModel> SaveRotationGroupAsync(int lotId, int? id, RotationGroupInput input)
    {
        await EnsureManagerAsync(lotId);
        return await WriteAsync(async () => {
            if (input.OffsetDays < 0 || input.OffsetDays > 365 || input.Members == null || input.Members.Any(m => m == null) || input.Members.Count > 500
                || input.Members.Select(m => m.EmployeeId).Distinct().Count() != input.Members.Count)
                throw new BadRequestException("Choose unique members and an offset from 0 to 365 days.");
            var eligible = await StaffAsync(lotId);
            foreach (var member in input.Members)
            {
                if (!eligible.Any(e => e.EmployeeId == member.EmployeeId))
                    throw new BadRequestException("Every group member must be an active Staff member at this lot.");
                if (input.IsActive && await (from m in db.WorkRotationMembers join g in db.WorkRotationGroups on m.WorkRotationGroupId equals g.Id
                    where g.ParkingLotId == lotId && g.IsActive && g.Id != id && m.EmployeeId == member.EmployeeId select m).AnyAsync())
                    throw new BadRequestException("A Staff member can belong to only one active rotation group at a lot.");
            }
            var group = id.HasValue ? await db.WorkRotationGroups.SingleOrDefaultAsync(g => g.Id == id && g.ParkingLotId == lotId)
                ?? throw new NotFoundException("Rotation group not found.") : new WorkRotationGroup { ParkingLotId = lotId };
            if (id.HasValue) { CheckRevision(group.Revision, input.Revision); group.Revision++; }
            else db.WorkRotationGroups.Add(group);
            group.Name = Name(input.Name); group.OffsetDays = input.OffsetDays; group.IsActive = input.IsActive;
            await SaveWorkChangesAsync();
            var old = await db.WorkRotationMembers.Where(m => m.WorkRotationGroupId == group.Id).ToListAsync();
            foreach (var member in old.Where(m => !input.Members.Any(x => x.EmployeeId == m.EmployeeId))) db.WorkRotationMembers.Remove(member);
            foreach (var member in input.Members)
            {
                if (!old.Any(m => m.EmployeeId == member.EmployeeId))
                    db.WorkRotationMembers.Add(new WorkRotationMember { WorkRotationGroupId = group.Id, EmployeeId = member.EmployeeId });
            }
            await SaveWorkChangesAsync();
            return new RotationGroupModel(group.Id, group.Name, group.OffsetDays, group.IsActive, group.Revision, input.Members);
        });
    }

    public async Task<IReadOnlyList<RotationCycleModel>> RotationCyclesAsync(int lotId)
    {
        await EnsureManagerAsync(lotId);
        return (await db.WorkRotationCycles.AsNoTracking().Where(c => c.ParkingLotId == lotId).OrderBy(c => c.Id).ToListAsync())
            .Select(c => new RotationCycleModel(c.Id, c.Name, c.Revision, JsonSerializer.Deserialize<List<RotationStep>>(c.StepsJson))).ToList();
    }

    public async Task<RotationCycleModel> SaveRotationCycleAsync(int lotId, int? id, RotationCycleInput input)
    {
        await EnsureManagerAsync(lotId);
        return await WriteAsync(async () => {
            if (input.Steps == null || input.Steps.Any(s => s == null) || input.Steps.Count == 0 || input.Steps.Count > 93
                || input.Steps.Any(s => s.Days < 1 || s.Days > 31) || input.Steps.Sum(s => s.Days) > 93 || input.Steps.All(s => s.TemplateId == null))
                throw new BadRequestException("A cycle needs work blocks and optional rest blocks, from 1 to 93 days in total.");
            foreach (var templateId in input.Steps.Where(s => s.TemplateId.HasValue).Select(s => s.TemplateId.Value).Distinct())
                if (!await db.WorkShiftTemplates.AnyAsync(t => t.Id == templateId && t.ParkingLotId == lotId && t.IsActive))
                    throw new BadRequestException("Choose active templates at this lot.");
            var cycle = id.HasValue ? await db.WorkRotationCycles.SingleOrDefaultAsync(c => c.Id == id && c.ParkingLotId == lotId)
                ?? throw new NotFoundException("Rotation cycle not found.") : new WorkRotationCycle { ParkingLotId = lotId };
            if (id.HasValue) { CheckRevision(cycle.Revision, input.Revision); cycle.Revision++; } else db.WorkRotationCycles.Add(cycle);
            cycle.Name = Name(input.Name); cycle.StepsJson = JsonSerializer.Serialize(input.Steps);
            await SaveWorkChangesAsync();
            return new RotationCycleModel(cycle.Id, cycle.Name, cycle.Revision, input.Steps);
        });
    }

    private record RotationInterval(int AssignmentId, int EmployeeId, string UserId, DutyRosterShift Shift, DutyAssignment Assignment, DutyRoster Roster);
    private record PlannedDuty(int EmployeeId, string StaffName, int GroupId, string Key, DutyRosterShift Shift);
    private record RotationPlan(RotationPreviewModel Preview, List<PlannedDuty> Add, List<DutyAssignment> Remove,
        List<DutyStandbyAssignment> AddStandby, List<DutyStandbyAssignment> RemoveStandby, List<DutyRosterShift> Shifts);

    private async Task<List<RotationInterval>> RotationIntervalsAsync(int lotId, DateOnly fromDate, DateOnly toDate)
    {
        var zone = RotationZone(await db.ParkingLots.Where(l => l.Id == lotId).Select(l => l.TimeZoneId).SingleAsync());
        var rest = (double)(RotationRules.MinimumRestHours ?? 0);
        var from = DayBounds(WeekStart(fromDate), zone).Start.AddHours(-rest);
        var to = DayBounds(WeekStart(toDate).AddDays(13), zone).End.AddHours(rest);
        var users = await db.Employees.Where(e => e.ParkingLotId == lotId).Select(e => e.UserId).ToListAsync();
        return await (
        from a in db.DutyAssignments.AsNoTracking() join s in db.DutyRosterShifts.AsNoTracking() on a.DutyRosterShiftId equals s.Id
        join r in db.DutyRosters.AsNoTracking() on s.DutyRosterId equals r.Id
        join e in db.Employees.AsNoTracking() on a.EmployeeId equals e.Id
        where !a.IsCancelled && !s.IsCancelled && users.Contains(e.UserId) && s.StartsAt < to && s.EndsAt > @from orderby a.Id
        select new RotationInterval(a.Id, e.Id, e.UserId, s, a, r)).ToListAsync();
    }

    private async Task<IReadOnlyList<StandbyModel>> StandbysAsync(int rosterId) => await (
        from s in db.DutyStandbyAssignments.AsNoTracking() join e in db.Employees on s.EmployeeId equals e.Id
        where s.DutyRosterId == rosterId && !s.IsCancelled orderby s.WorkDate, s.Id
        select new StandbyModel(s.Id, s.WorkDate, s.EmployeeId, e.FullName, s.Source)).ToListAsync();

    private static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
    private static TimeZoneInfo RotationZone(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);
    private static (DateTime Start, DateTime End) DayBounds(DateOnly day, TimeZoneInfo zone) =>
        (TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), zone), TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue), zone));

    private List<RotationIssue> CheckRotationInterval(int employeeId, string userId, DutyRosterShift shift,
        IEnumerable<RotationInterval> all, ApprovedWorkLeaveResult leave)
    {
        var issues = new List<RotationIssue>();
        void Add(string code, string text) => issues.Add(new RotationIssue(code, shift.WorkDate, employeeId, text));
        if (leave.Leaves.Any(l => l.EmployeeId == employeeId && l.StartsAt < shift.EndsAt && shift.StartsAt < l.EndsAt))
            Add("ApprovedLeave", "Approved leave overlaps this shift; no assignment proposed.");
        var own = all.Where(a => a.UserId == userId).ToList();
        if (own.Any(a => a.Shift.StartsAt < shift.EndsAt && shift.StartsAt < a.Shift.EndsAt)) Add("Overlap", "Overlapping assignment, including another draft or published roster.");
        if (RotationRules.MinimumRestHours.HasValue && own.Any(a =>
            (a.Shift.EndsAt <= shift.StartsAt && (shift.StartsAt - a.Shift.EndsAt).TotalHours < (double)RotationRules.MinimumRestHours.Value)
            || (shift.EndsAt <= a.Shift.StartsAt && (a.Shift.StartsAt - shift.EndsAt).TotalHours < (double)RotationRules.MinimumRestHours.Value)))
            Add("MinimumRest", $"Less than {RotationRules.MinimumRestHours} hours between shifts.");
        if (RotationRules.MaximumWeeklyHours.HasValue)
        {
            var zone = RotationZone(shift.TimeZoneId);
            var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(shift.EndsAt.AddTicks(-1), zone));
            for (var week = WeekStart(shift.WorkDate); week <= lastDay; week = week.AddDays(7))
            {
                var from = DayBounds(week, zone).Start; var to = DayBounds(week.AddDays(6), zone).End;
                var total = own.Select(x => x.Shift).Append(shift).Sum(x => Math.Max(0,
                    ((x.EndsAt < to ? x.EndsAt : to) - (x.StartsAt > from ? x.StartsAt : from)).TotalHours));
                if (total > (double)RotationRules.MaximumWeeklyHours.Value + 0.000001)
                    Add("WeeklyHours", $"Week starting {week:yyyy-MM-dd} exceeds {RotationRules.MaximumWeeklyHours} planned hours (overnight hours split at the week boundary).");
            }
        }
        return issues;
    }

    private void AddConfigurationWarnings(List<RotationIssue> issues, ApprovedWorkLeaveResult leave)
    {
        if (!RotationRules.MinimumRestHours.HasValue || !RotationRules.MaximumWeeklyHours.HasValue)
            issues.Add(new("RulesMissing", null, null, "Rest/weekly limits are not fully configured; those checks are incomplete.", false));
        if (!leave.Available) issues.Add(new("LeaveSourceUnavailable", null, null, "Approved-leave data is not connected. Leave exclusions could not be verified; review leave before publication.", false));
    }

    private async Task<IReadOnlyList<RotationIssue>> ValidateRotationRosterAsync(DutyRoster roster)
    {
        var all = await RotationIntervalsAsync(roster.ParkingLotId, roster.FromDate, roster.ToDate);
        var own = all.Where(a => a.Roster.Id == roster.Id).ToList();
        var zoneId = await db.ParkingLots.Where(l => l.Id == roster.ParkingLotId).Select(l => l.TimeZoneId).SingleAsync();
        var zone = RotationZone(zoneId);
        var leave = await LeaveSource.ReadAsync(roster.ParkingLotId, DayBounds(roster.FromDate, zone).Start, DayBounds(roster.ToDate.AddDays(1), zone).End);
        var issues = new List<RotationIssue>(); AddConfigurationWarnings(issues, leave);
        foreach (var row in own) issues.AddRange(CheckRotationInterval(row.EmployeeId, row.UserId, row.Shift, all.Where(a => a.AssignmentId != row.AssignmentId), leave)
            .Where(i => i.Code != "WeeklyHours" || !row.Assignment.AllowWeeklyOvertime));
        var standbys = await StandbysAsync(roster.Id);
        var staff = await AvailableSchedulingStaffAsync(roster.ParkingLotId);
        foreach (var row in standbys)
        {
            var bounds = DayBounds(row.WorkDate, zone);
            var user = staff.FirstOrDefault(e => e.EmployeeId == row.EmployeeId);
            if (user == null || all.Any(a => a.UserId == user.UserId && a.Shift.StartsAt < bounds.End && bounds.Start < a.Shift.EndsAt)
                || leave.Leaves.Any(l => l.EmployeeId == row.EmployeeId && l.StartsAt < bounds.End && bounds.Start < l.EndsAt))
                issues.Add(new("StandbyUnavailable", row.WorkDate, row.EmployeeId, "Standby must be active, free of direct work and approved leave throughout the local day."));
        }
        return issues.Distinct().ToList();
    }

    public async Task<RotationPreviewModel> PreviewRotationAsync(int lotId, int rosterId, RotationGenerationInput input)
    {
        await EnsureManagerAsync(lotId);
        return (await PrepareRotationAsync(lotId, rosterId, input)).Preview;
    }

    private async Task<RotationPlan> PrepareRotationAsync(int lotId, int rosterId, RotationGenerationInput input)
    {
        var roster = await FindRosterAsync(lotId, rosterId); CheckRevision(roster.Revision, input.Revision);
        input.StandbyGroupIds ??= [];
        if (roster.Status != "Draft") throw new BadRequestException("Rotation generation can only target an existing Draft roster.");
        if (input.FromDate == default || input.CycleStartDate == default || input.ToDate < input.FromDate
            || input.FromDate < roster.FromDate || input.ToDate > roster.ToDate || input.ToDate.DayNumber - input.FromDate.DayNumber > 92
            || input.GroupIds == null || input.GroupIds.Count == 0 || input.GroupIds.Distinct().Count() != input.GroupIds.Count)
            throw new BadRequestException("Choose groups, a cycle start and a range within this roster (at most 93 days).");
        var cycle = (await RotationCyclesAsync(lotId)).SingleOrDefault(c => c.Id == input.CycleId) ?? throw new NotFoundException("Rotation cycle not found.");
        var groups = (await RotationGroupsAsync(lotId)).Where(g => input.GroupIds.Contains(g.Id) && g.IsActive).ToList();
        if (groups.Count != input.GroupIds.Count) throw new BadRequestException("Choose active groups from this lot.");
        var templateIds = cycle.Steps.Where(s => s.TemplateId.HasValue).Select(s => s.TemplateId.Value).Distinct().ToList();
        var templates = (await TemplatesAsync(lotId)).Where(t => templateIds.Contains(t.Id) && t.IsActive).ToList();
        if (templates.Count != templateIds.Count) throw new BadRequestException("A cycle template is inactive or unavailable.");
        var staff = await StaffAsync(lotId); var people = staff.ToDictionary(e => e.EmployeeId);
        var all = await RotationIntervalsAsync(lotId, roster.FromDate, roster.ToDate);
        var attendanceIds = await db.DutyAttendances.AsNoTracking().Select(a => a.DutyAssignmentId).ToListAsync();
        var historyIds = await db.DutyAttendanceEvents.AsNoTracking().Select(a => a.DutyAssignmentId).Distinct().ToListAsync();
        var pendingIds = await db.WorkLeaveRequests.Where(l => l.Status == "Pending").Select(l => l.DutyAssignmentId).ToListAsync();
        pendingIds.AddRange(await db.WorkSwapRequests.Where(s => s.Status == "AwaitingColleague" || s.Status == "PendingManager").Select(s => s.AssignmentId).ToListAsync());
        pendingIds.AddRange(await db.WorkSwapRequests.Where(s => s.Status == "AwaitingColleague" || s.Status == "PendingManager").Select(s => s.OtherAssignmentId).ToListAsync());
        bool Protected(RotationInterval row) => pendingIds.Contains(row.AssignmentId) || attendanceIds.Contains(row.AssignmentId) || historyIds.Contains(row.AssignmentId)
            || (row.Roster.Status == "Published" && row.Roster.PublishedAt.HasValue
                && row.Roster.PublishedAt <= row.Shift.StartsAt.AddMinutes(attendanceRules.AbsentAfterMinutes)
                && row.Assignment.AttendanceTrackingFrom <= row.Shift.StartsAt.AddMinutes(attendanceRules.AbsentAfterMinutes)
                && Now > row.Shift.StartsAt.AddMinutes(attendanceRules.AbsentAfterMinutes));
        var changes = new List<RotationChange>(); var issues = new List<RotationIssue>();
        var removals = all.Where(a => a.Roster.Id == rosterId && a.Shift.WorkDate >= input.FromDate && a.Shift.WorkDate <= input.ToDate
            && a.Assignment.Source == "Rotation" && !a.Assignment.IsManuallyEdited && a.Assignment.RotationGroupId.HasValue
            && input.GroupIds.Contains(a.Assignment.RotationGroupId.Value) && input.ReplaceGenerated && !Protected(a)).Select(a => a.Assignment).ToList();
        var removedIds = removals.Select(a => a.Id).ToHashSet();
        var working = all.Where(a => !removedIds.Contains(a.AssignmentId)).ToList();
        foreach (var row in all.Where(a => a.Roster.ParkingLotId == lotId && a.Shift.WorkDate >= input.FromDate && a.Shift.WorkDate <= input.ToDate))
            changes.Add(new(removedIds.Contains(row.AssignmentId) ? "Replace" : "Keep", row.Shift.WorkDate, row.EmployeeId,
                (await db.Employees.FindAsync(row.EmployeeId)).FullName,
                $"{row.Shift.Name} · {row.Roster.Status}" + (row.Assignment.IsManuallyEdited ? " · manually edited" : Protected(row) ? " · attendance protected" : ""), row.AssignmentId));
        var preservedKeys = await db.DutyAssignments.AsNoTracking().Where(a => a.IsManuallyEdited && a.GenerationKey != null)
            .Select(a => a.GenerationKey).ToListAsync();
        var model = await ModelAsync(roster); var shifts = model.Shifts.ToList(); var newShifts = new List<DutyRosterShift>(); var additions = new List<PlannedDuty>();
        var zoneId = await db.ParkingLots.Where(l => l.Id == lotId).Select(l => l.TimeZoneId).SingleAsync(); var zone = RotationZone(zoneId);
        var leave = await LeaveSource.ReadAsync(lotId, DayBounds(input.FromDate, zone).Start, DayBounds(input.ToDate.AddDays(1), zone).End);
        AddConfigurationWarnings(issues, leave);
        var days = cycle.Steps.SelectMany(s => Enumerable.Repeat(s.TemplateId, s.Days)).ToArray();
        var allStandby = await db.DutyStandbyAssignments.AsNoTracking().Where(s => !s.IsCancelled).OrderBy(s => s.Id).ToListAsync();
        var lotRosterIds = await db.DutyRosters.Where(r => r.ParkingLotId == lotId).Select(r => r.Id).ToListAsync();
        for (var day = input.FromDate; day <= input.ToDate; day = day.AddDays(1))
        {
            // Materialize every cycle template's staffing demand, even when all selected groups are off.
            foreach (var template in templates)
            {
                if (shifts.Any(s => s.WorkDate == day && s.WorkShiftTemplateId == template.Id)) continue;
                var snapshot = await TemplateSnapshotAsync(lotId, rosterId, day, template.Id);
                snapshot.Id = -(newShifts.Count + 1); newShifts.Add(snapshot);
                shifts.Add(new RosterShiftModel(snapshot.Id, template.Id, day, snapshot.Name, snapshot.TimeZoneId, snapshot.StartTime, snapshot.EndTime,
                    Utc(snapshot.StartsAt), Utc(snapshot.EndsAt), snapshot.MinimumStaff, []));
            }
            foreach (var group in groups.OrderBy(g => g.Id))
            {
                var index = ((day.DayNumber - input.CycleStartDate.DayNumber + group.OffsetDays) % days.Length + days.Length) % days.Length;
                var templateId = days[index]; if (!templateId.HasValue) continue;
                var occurrence = shifts.First(s => s.WorkDate == day && s.WorkShiftTemplateId == templateId.Value);
                var snapshot = newShifts.FirstOrDefault(s => s.Id == occurrence.Id) ?? await db.DutyRosterShifts.AsNoTracking().SingleAsync(s => s.Id == occurrence.Id);
                foreach (var member in group.Members.OrderBy(m => m.EmployeeId))
                {
                    var key = $"{group.Id}:{member.EmployeeId}:{day:yyyy-MM-dd}";
                    if (preservedKeys.Contains(key)) { changes.Add(new("KeepManual", day, member.EmployeeId, people.GetValueOrDefault(member.EmployeeId)?.FullName ?? "Staff", "Manual edit or cancellation retained.")); continue; }
                    if (!people.TryGetValue(member.EmployeeId, out var person)) { issues.Add(new("Unavailable", day, member.EmployeeId, "Group member is no longer active at this lot.")); continue; }
                    if (working.Any(a => a.Roster.Id == rosterId && a.EmployeeId == member.EmployeeId && a.Shift.Id == snapshot.Id)) continue;
                    var conflicts = CheckRotationInterval(person.EmployeeId, person.UserId, snapshot, working, leave);
                    if (allStandby.Any(s => s.EmployeeId == person.EmployeeId
                        && !(input.ReplaceGenerated && input.IncludeStandby && s.DutyRosterId == rosterId && s.Source == "Rotation" && s.WorkDate >= input.FromDate && s.WorkDate <= input.ToDate)
                        && DayBounds(s.WorkDate, zone).Start < snapshot.EndsAt && snapshot.StartsAt < DayBounds(s.WorkDate, zone).End))
                        conflicts.Add(new("StandbyConflict", day, person.EmployeeId, "Existing standby overlaps the proposed work day."));
                    if (conflicts.Count > 0) { issues.AddRange(conflicts); continue; }
                    additions.Add(new(person.EmployeeId, person.FullName, group.Id, key, snapshot));
                    changes.Add(new("Add", day, person.EmployeeId, person.FullName, snapshot.Name));
                    working.Add(new RotationInterval(-additions.Count, person.EmployeeId, person.UserId, snapshot, new DutyAssignment(), roster));
                }
            }
        }
        var standbyRows = await db.DutyStandbyAssignments.AsNoTracking().Where(s => s.DutyRosterId == rosterId).ToListAsync();
        var removeStandby = input.ReplaceGenerated && input.IncludeStandby ? standbyRows.Where(s => !s.IsCancelled && s.Source == "Rotation" && s.WorkDate >= input.FromDate && s.WorkDate <= input.ToDate).ToList() : [];
        var addedStandby = new List<DutyStandbyAssignment>();
        if (input.IncludeStandby)
        {
            var poolGroups = (await RotationGroupsAsync(lotId)).Where(g => g.IsActive && input.StandbyGroupIds.Contains(g.Id)).ToList();
            if (poolGroups.Count != input.StandbyGroupIds.Distinct().Count() || poolGroups.Count == 0) throw new BadRequestException("Choose an active standby group pool at this lot.");
            var pool = poolGroups.SelectMany(g => g.Members).Select(m => m.EmployeeId).Distinct().OrderBy(id => id).ToList();
            for (var day = input.FromDate; day <= input.ToDate; day = day.AddDays(1))
            {
                var retained = allStandby.Where(s => lotRosterIds.Contains(s.DutyRosterId) && s.WorkDate == day && !removeStandby.Any(x => x.Id == s.Id)).ToList();
                if (retained.Count > 0) {
                    foreach (var row in retained) changes.Add(new("KeepStandby", day, row.EmployeeId, people.GetValueOrDefault(row.EmployeeId)?.FullName ?? "Staff", "Existing standby retained; one per lot/day."));
                    continue;
                }
                if (standbyRows.Any(s => s.WorkDate == day && (s.Source == "Manual" || (!s.IsCancelled && !removeStandby.Any(x => x.Id == s.Id))))) continue;
                var bounds = DayBounds(day, zone); var dayOffset = day.DayNumber - input.CycleStartDate.DayNumber;
                var candidates = pool.Skip(pool.Count == 0 ? 0 : ((dayOffset % pool.Count) + pool.Count) % pool.Count).Concat(pool.Take(pool.Count == 0 ? 0 : ((dayOffset % pool.Count) + pool.Count) % pool.Count));
                var chosen = candidates.FirstOrDefault(id => people.TryGetValue(id, out var p)
                    && !working.Any(a => a.UserId == p.UserId && a.Shift.StartsAt < bounds.End && bounds.Start < a.Shift.EndsAt)
                    && !leave.Leaves.Any(l => l.EmployeeId == id && l.StartsAt < bounds.End && bounds.Start < l.EndsAt)
                    && !allStandby.Any(s => s.EmployeeId == id && s.WorkDate == day && !removeStandby.Any(x => x.Id == s.Id)));
                if (chosen == 0) { issues.Add(new("StandbyShortage", day, null, "No eligible standby for this day.", false)); continue; }
                addedStandby.Add(new DutyStandbyAssignment { DutyRosterId = rosterId, EmployeeId = chosen, WorkDate = day });
                changes.Add(new("AddStandby", day, chosen, people[chosen].FullName, "Standby only; excluded from direct staffing and planned work hours."));
            }
        }
        foreach (var s in removeStandby) changes.Add(new("ReplaceStandby", s.WorkDate, s.EmployeeId, people.GetValueOrDefault(s.EmployeeId)?.FullName ?? "Staff", "Generated standby replaced."));
        var projected = shifts.Select(s => {
            var assigned = s.Assignments.Where(a => !removedIds.Contains(a.Id)).Concat(additions.Where(a => a.Shift.Id == s.Id)
                .Select(a => new AssignmentModel(-additions.IndexOf(a) - 1, a.EmployeeId, a.StaffName) { Source = "Rotation" })).ToList();
            return s with { Assignments = assigned, Assigned = assigned.Count };
        }).ToList();
        var shortages = Shortages(projected);
        var projectedStandby = (await StandbysAsync(rosterId)).Where(s => !removeStandby.Any(x => x.Id == s.Id)).Concat(addedStandby.Select((s, i) => new StandbyModel(-i - 1, s.WorkDate, s.EmployeeId, people[s.EmployeeId].FullName, s.Source))).ToList();
        var projectedRoster = model with { Shifts = projected, Shortages = shortages, Standbys = projectedStandby, RotationIssues = issues };
        var assignedIds = projected.Where(s => s.WorkDate >= input.FromDate && s.WorkDate <= input.ToDate).SelectMany(s => s.Assignments).Select(a => a.EmployeeId).ToHashSet();
        var unassigned = staff.Where(e => !assignedIds.Contains(e.EmployeeId)).ToList();
        // Hash authoritative inputs, not a client-supplied proposal. No time-varying display timestamps in the fingerprint.
        var fingerprint = JsonSerializer.Serialize(new { roster.Id, roster.Revision, input.FromDate, input.ToDate, input.CycleStartDate,
            input.GroupIds, input.StandbyGroupIds, input.ReplaceGenerated, input.IncludeStandby, cycle, groups, templates, staff, leave, Rules = RotationRules,
            Existing = all.Select(a => new { a.Assignment, a.Shift, a.Roster.Id, a.Roster.Revision, a.Roster.Status, Protected = Protected(a) }),
            Standby = allStandby, Changes = changes, Issues = issues });
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
        return new(new RotationPreviewModel(projectedRoster, token, additions.Count, changes, issues, unassigned,
            RotationRules.MinimumRestHours, RotationRules.MaximumWeeklyHours, leave.Available), additions, removals, addedStandby, removeStandby, newShifts);
    }

    public Task<DutyRosterModel> ApplyRotationAsync(int lotId, int rosterId, RotationGenerationInput input) =>
        MutateAsync(lotId, rosterId, input, "RotationGenerated", async roster => {
            var plan = await PrepareRotationAsync(lotId, rosterId, input);
            if (string.IsNullOrEmpty(input.PreviewToken) || input.PreviewToken != plan.Preview.Token)
                throw new ConflictException("Preview inputs or scheduling data changed. Generate a fresh preview before saving.");
            foreach (var row in plan.Remove)
            {
                await EnsureAttendanceScheduleEditableAsync(row.Id);
                (await db.DutyAssignments.SingleAsync(a => a.Id == row.Id)).IsCancelled = true;
            }
            var shiftMap = new Dictionary<int, int>();
            foreach (var shift in plan.Shifts)
            {
                var key = shift.Id; shift.Id = 0; db.DutyRosterShifts.Add(shift); await SaveWorkChangesAsync(); shiftMap[key] = shift.Id;
            }
            foreach (var duty in plan.Add) db.DutyAssignments.Add(new DutyAssignment {
                DutyRosterShiftId = shiftMap.GetValueOrDefault(duty.Shift.Id, duty.Shift.Id), EmployeeId = duty.EmployeeId,
                Source = "Rotation", RotationGroupId = duty.GroupId, GenerationKey = duty.Key, AttendanceTrackingFrom = Now });
            foreach (var row in plan.RemoveStandby) (await db.DutyStandbyAssignments.SingleAsync(s => s.Id == row.Id)).IsCancelled = true;
            db.DutyStandbyAssignments.AddRange(plan.AddStandby);
            roster.HasRotation = true;
            roster.RotationConfigurationJson = JsonSerializer.Serialize(new { input.FromDate, input.ToDate, input.CycleStartDate,
                input.GroupIds, input.StandbyGroupIds, input.CycleId, input.ReplaceGenerated, input.IncludeStandby,
                PreviewToken = plan.Preview.Token, Rules = RotationRules,
                Groups = await RotationGroupsAsync(lotId), Cycles = await RotationCyclesAsync(lotId) });
        });

    public Task<DutyRosterModel> SaveStandbyAsync(int lotId, int rosterId, int? id, StandbyMutationInput input) =>
        MutateAsync(lotId, rosterId, input, "StandbyChanged", async roster => {
            if (input.WorkDate < roster.FromDate || input.WorkDate > roster.ToDate) throw new BadRequestException("Standby date must be inside this roster.");
            var row = id.HasValue ? await db.DutyStandbyAssignments.SingleOrDefaultAsync(s => s.Id == id && s.DutyRosterId == rosterId && !s.IsCancelled)
                ?? throw new NotFoundException("Standby not found.") : new DutyStandbyAssignment { DutyRosterId = rosterId };
            if (input.EmployeeId.HasValue)
            {
                var person = (await StaffAsync(lotId)).FirstOrDefault(e => e.EmployeeId == input.EmployeeId.Value) ?? throw new BadRequestException("Choose active Staff at this lot.");
                var zone = RotationZone(await db.ParkingLots.Where(l => l.Id == lotId).Select(l => l.TimeZoneId).SingleAsync()); var bounds = DayBounds(input.WorkDate, zone);
                var all = await RotationIntervalsAsync(lotId, roster.FromDate, roster.ToDate); var leave = await LeaveSource.ReadAsync(lotId, bounds.Start, bounds.End);
                if (all.Any(a => a.UserId == person.UserId && a.Shift.StartsAt < bounds.End && bounds.Start < a.Shift.EndsAt)
                    || leave.Leaves.Any(l => l.EmployeeId == person.EmployeeId && l.StartsAt < bounds.End && bounds.Start < l.EndsAt))
                    throw new BadRequestException("Standby overlaps direct work or approved leave.");
                if (await (from s in db.DutyStandbyAssignments join r in db.DutyRosters on s.DutyRosterId equals r.Id
                    where !s.IsCancelled && s.Id != id && r.ParkingLotId == lotId && s.WorkDate == input.WorkDate select s).AnyAsync())
                    throw new BadRequestException("This lot already has standby on this date.");
                row.EmployeeId = person.EmployeeId;
            }
            else if (!id.HasValue) throw new BadRequestException("Choose a Staff member.");
            row.WorkDate = input.WorkDate; row.Source = "Manual"; row.IsCancelled = !input.EmployeeId.HasValue;
            if (!id.HasValue) db.DutyStandbyAssignments.Add(row);
            roster.HasRotation = true;
        });
}
