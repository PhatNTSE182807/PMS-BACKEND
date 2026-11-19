using System.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.Application.Jobs;
using PMS.Application.Models.DutySchedule;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>
/// The duty board of a lot: 4 fixed 6-hour shifts a day, one Staff member each. A weekly pattern repeats
/// until the Manager changes it; a one-day change replaces one shift on one date. Attendance, leave and
/// the shift check of the POS are in the other parts of this class.
/// </summary>
public partial class DutyScheduleService(PmsDbContext db, DatabaseContext identity, LotScopeService scope,
    TimeProvider clock, DutyOptions options, PayrollOptions pay, IBackgroundJobClient jobs = null)
{
    private const string StaffRole = "Staff";
    private const string ActiveStatus = "Active";
    private const string DayScope = "Day";
    private const string FollowingScope = "Following";

    private readonly List<UserNotification> _notices = [];
    private readonly List<(int LotId, long[] NoticeIds)> _publish = [];

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /* Lot, time and slots ------------------------------------------------ */

    private sealed record LotInfo(int Id, string Name, string ManagerUserId, TimeZoneInfo Zone);

    private async Task<LotInfo> LotAsync(int lotId)
    {
        var lot = await db.ParkingLots.AsNoTracking().Where(l => l.Id == lotId && !l.IsDeleted)
            .Select(l => new { l.Id, l.Name, l.ManagerUserId, l.TimeZoneId }).SingleOrDefaultAsync()
            ?? throw new NotFoundException("Parking lot not found.");
        return new LotInfo(lot.Id, lot.Name, lot.ManagerUserId, ParkingText.FindTimeZone(lot.TimeZoneId));
    }

    private DateOnly Today(LotInfo lot) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Now, lot.Zone));

    private static DateTime SlotStart(LotInfo lot, DateOnly date, int slot) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date.ToDateTime(DutySlots.Start(slot)), DateTimeKind.Unspecified), lot.Zone);

    private static DateTime SlotEnd(LotInfo lot, DateOnly date, int slot) => SlotStart(lot, date, slot).AddHours(DutySlots.Hours);

    /// <summary>1 = Monday … 7 = Sunday.</summary>
    private static int IsoDay(DateOnly date) => ((int)date.DayOfWeek + 6) % 7 + 1;

    private static DateOnly WeekStart(DateOnly date) => date.AddDays(1 - IsoDay(date));

    private static void CheckSlot(int slot)
    {
        if (slot is < 0 or >= DutySlots.Count) throw new BadRequestException("Shift must be 0 (00–06), 1 (06–12), 2 (12–18) or 3 (18–24).");
    }

    /* Who works when ----------------------------------------------------- */

    /// <summary>The person on one shift, and where that comes from.</summary>
    private sealed record Assignment(int? EmployeeId, DutyOverride Override, int? PatternEmployeeId, DateTime? AssignedAt);

    /// <summary>Effective assignments of a lot for a date range: the pattern of each date with its one-day changes.</summary>
    private sealed class Schedule
    {
        public readonly Dictionary<(DateOnly, int), Assignment> Cells = [];
        public List<(DutyPattern Pattern, Dictionary<(int, int), int> Cells)> Patterns = [];

        public Assignment At(DateOnly date, int slot) => Cells.TryGetValue((date, slot), out var a) ? a : new Assignment(null, null, null, null);

        public int? EmployeeAt(DateOnly date, int slot) => At(date, slot).EmployeeId;

        public (DutyPattern Pattern, Dictionary<(int, int), int> Cells) PatternOn(DateOnly date) =>
            Patterns.LastOrDefault(p => p.Pattern.EffectiveFrom <= date);

        /// <summary>Shifts of a person in the weekly pattern that applies to a date.</summary>
        public IEnumerable<int> PatternSlots(int employeeId, DateOnly date)
        {
            var p = PatternOn(date);
            if (p.Pattern == null) yield break;
            for (var slot = 0; slot < DutySlots.Count; slot++)
                if (p.Cells.TryGetValue((IsoDay(date), slot), out var e) && e == employeeId) yield return slot;
        }
    }

    private async Task<Schedule> LoadScheduleAsync(int lotId, DateOnly from, DateOnly to)
    {
        var patterns = await db.DutyPatterns.AsNoTracking().Where(p => p.ParkingLotId == lotId && p.EffectiveFrom <= to)
            .OrderBy(p => p.EffectiveFrom).ToListAsync();
        var ids = patterns.Select(p => p.Id).ToList();
        var cells = await db.DutyPatternCells.AsNoTracking().Where(c => ids.Contains(c.DutyPatternId)).ToListAsync();
        var overrides = await db.DutyOverrides.AsNoTracking()
            .Where(o => o.ParkingLotId == lotId && o.WorkDate >= from && o.WorkDate <= to).ToListAsync();

        var schedule = new Schedule
        {
            Patterns = patterns.Select(p => (p, cells.Where(c => c.DutyPatternId == p.Id)
                .ToDictionary(c => (c.DayOfWeek, c.Slot), c => c.EmployeeId))).ToList()
        };
        var byCell = overrides.ToDictionary(o => (o.WorkDate, o.Slot));
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var pattern = schedule.PatternOn(date);
            for (var slot = 0; slot < DutySlots.Count; slot++)
            {
                int? fromPattern = pattern.Pattern != null && pattern.Cells.TryGetValue((IsoDay(date), slot), out var e) ? e : null;
                byCell.TryGetValue((date, slot), out var change);
                schedule.Cells[(date, slot)] = new Assignment(change != null ? change.EmployeeId : fromPattern, change,
                    fromPattern, change?.CreatedAt ?? pattern.Pattern?.CreatedAt);
            }
        }
        return schedule;
    }

    /* Staff of a lot ----------------------------------------------------- */

    private sealed record Person(int EmployeeId, string UserId, string FullName, string EmployeeCode, string EmploymentType,
        int? ParkingLotId, DateOnly? HireDate, bool IsActive);

    /// <summary>Staff members of a lot (active or not) plus anyone else the given ids point to.</summary>
    private async Task<Dictionary<int, Person>> PeopleAsync(int lotId, IEnumerable<int> extraIds = null)
    {
        var extra = extraIds?.Distinct().ToList() ?? [];
        var employees = await db.Employees.AsNoTracking()
            .Where(e => (e.ParkingLotId == lotId && e.Position != "Manager") || extra.Contains(e.Id)).ToListAsync();
        var userIds = employees.Select(e => e.UserId).ToList();
        var users = await identity.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Role, u.IsActive }).ToDictionaryAsync(u => u.Id);
        return employees
            .Where(e => users.TryGetValue(e.UserId, out var u) && u.Role == StaffRole)
            .ToDictionary(e => e.Id, e => new Person(e.Id, e.UserId, e.FullName, e.EmployeeCode,
                EmploymentTypes.IsValid(e.EmploymentType) ? e.EmploymentType : EmploymentTypes.FullTime, e.ParkingLotId, e.HireDate,
                e.Status == ActiveStatus && users[e.UserId].IsActive && e.ParkingLotId == lotId));
    }

    private async Task<Person> CurrentStaffAsync()
    {
        if (scope.Role != StaffRole) throw new ForbiddenException("Only Staff have a duty schedule.");
        var userId = scope.UserId;
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.UserId == userId && e.Status != "Terminated")
            ?? throw new NotFoundException("No employee record for this account.");
        if (employee.ParkingLotId == null) throw new BadRequestException("You are not assigned to a parking lot yet.");
        var people = await PeopleAsync(employee.ParkingLotId.Value, [employee.Id]);
        return people.TryGetValue(employee.Id, out var me) ? me : throw new ForbiddenException("Only Staff have a duty schedule.");
    }

    private async Task EnsureManagerAsync(int lotId)
    {
        if (scope.Role != "Manager") throw new ForbiddenException("Only the lot's Manager manages the duty board.");
        await scope.EnsureCanManageAsync(lotId);
    }

    /// <summary>Pending and approved leave of the given people that touches a date range.</summary>
    private async Task<List<DutyLeaveRequest>> LeavesAsync(IEnumerable<int> employeeIds, DateOnly from, DateOnly to)
    {
        var ids = employeeIds.Distinct().ToList();
        return await db.DutyLeaveRequests.AsNoTracking()
            .Where(r => ids.Contains(r.EmployeeId) && (r.Status == "Pending" || r.Status == "Approved") && r.FromDate <= to && r.ToDate >= from)
            .ToListAsync();
    }

    /* Board -------------------------------------------------------------- */

    public async Task<DutyWeekModel> ManagerWeekAsync(int lotId, DateOnly? start)
    {
        await EnsureManagerAsync(lotId);
        return await WeekAsync(await LotAsync(lotId), start, viewer: null);
    }

    public async Task<DutyWeekModel> StaffWeekAsync(DateOnly? start)
    {
        var me = await CurrentStaffAsync();
        return await WeekAsync(await LotAsync(me.ParkingLotId!.Value), start, me);
    }

    private async Task<DutyWeekModel> WeekAsync(LotInfo lot, DateOnly? start, Person viewer)
    {
        var today = Today(lot);
        var weekStart = WeekStart(start ?? today);
        var weekEnd = weekStart.AddDays(6);
        var schedule = await LoadScheduleAsync(lot.Id, weekStart, weekEnd);
        var leaveIds = schedule.Cells.Values.Select(a => a.Override?.LeaveRequestId).Where(id => id != null).Select(id => id!.Value).Distinct().ToList();
        var leaveOwners = await db.DutyLeaveRequests.AsNoTracking().Where(r => leaveIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.EmployeeId);
        var people = await PeopleAsync(lot.Id, schedule.Cells.Values.Where(a => a.EmployeeId != null).Select(a => a.EmployeeId!.Value)
            .Concat(leaveOwners.Values));
        var attendance = await db.DutyShiftAttendances.AsNoTracking()
            .Where(a => a.ParkingLotId == lot.Id && a.WorkDate >= weekStart && a.WorkDate <= weekEnd).ToListAsync();
        var pending = (await LeavesAsync(people.Keys, weekStart, weekEnd)).Where(r => r.Status == "Pending").ToList();
        var isManager = viewer == null;

        var cells = new List<DutyCellModel>();
        for (var date = weekStart; date <= weekEnd; date = date.AddDays(1))
            for (var slot = 0; slot < DutySlots.Count; slot++)
            {
                var a = schedule.At(date, slot);
                people.TryGetValue(a.EmployeeId ?? 0, out var person);
                var mine = viewer != null && a.EmployeeId == viewer.EmployeeId;
                var record = a.EmployeeId == null ? null
                    : attendance.FirstOrDefault(x => x.WorkDate == date && x.Slot == slot && x.EmployeeId == a.EmployeeId);
                var showPrivate = isManager || mine;
                string onLeave = null;
                if (a.Override?.LeaveRequestId is int leaveId && leaveOwners.TryGetValue(leaveId, out var ownerId) && people.TryGetValue(ownerId, out var owner))
                    onLeave = owner.FullName;
                cells.Add(new DutyCellModel
                {
                    Date = date, Slot = slot, StartsAt = SlotStart(lot, date, slot), EndsAt = SlotEnd(lot, date, slot),
                    EmployeeId = a.EmployeeId, EmployeeName = person?.FullName, EmploymentType = person?.EmploymentType,
                    IsOneDayChange = a.Override != null && a.Override.EmployeeId != a.PatternEmployeeId,
                    OnLeaveName = onLeave,
                    LeavePending = showPrivate && a.EmployeeId != null && pending.Any(r => r.EmployeeId == a.EmployeeId && r.FromDate <= date && r.ToDate >= date),
                    AttendanceStatus = showPrivate ? record?.Status : null,
                    IsLate = showPrivate && IsLate(lot, record, a.AssignedAt),
                    CheckInAt = showPrivate ? record?.CheckInAt : null,
                    CheckOutAt = showPrivate ? record?.CheckOutAt : null,
                    IsPast = SlotEnd(lot, date, slot) <= Now,
                    IsMine = mine,
                });
            }

        return new DutyWeekModel
        {
            ParkingLotId = lot.Id, LotName = lot.Name, TimeZoneId = lot.Zone.Id, WeekStart = weekStart, Today = today, Cells = cells,
            Staff = isManager
                ? people.Values.Where(p => p.ParkingLotId == lot.Id)
                    .OrderBy(p => p.EmploymentType == EmploymentTypes.Backup).ThenBy(p => p.FullName)
                    .Select(p => new DutyStaffModel(p.EmployeeId, p.UserId, p.FullName, p.EmployeeCode, p.EmploymentType, p.IsActive)).ToList()
                : [],
            EmptyShifts = cells.Count(c => c.EmployeeId == null && !c.IsPast),
            PendingLeaveRequests = isManager ? await db.DutyLeaveRequests.CountAsync(r => r.ParkingLotId == lot.Id && r.Status == "Pending") : 0,
        };
    }

    /* Rules for putting someone on a shift ------------------------------- */

    /// <summary>Why a person cannot take a shift (null when they can), and a warning to show when they can.</summary>
    private static (string Blocked, string Warning) Check(Schedule schedule, Person person, DateOnly date, int slot,
        IReadOnlyCollection<DutyLeaveRequest> leaves, int? ignoreLeaveId = null)
    {
        if (!person.IsActive) return ("Not an active Staff member of this lot.", null);
        if (leaves.Any(r => r.EmployeeId == person.EmployeeId && r.Id != ignoreLeaveId && r.FromDate <= date && r.ToDate >= date))
            return ("On leave on this date.", null);

        bool Works(DateOnly d, int s) => !(d == date && s == slot) && schedule.EmployeeAt(d, s) == person.EmployeeId;
        (DateOnly, int) Step(int offset)
        {
            var index = slot + offset;
            var days = (int)Math.Floor(index / (double)DutySlots.Count);
            return (date.AddDays(days), index - days * DutySlots.Count);
        }
        bool At(int offset) { var (d, s) = Step(offset); return Works(d, s); }

        var sameDay = Enumerable.Range(0, DutySlots.Count).Count(s => Works(date, s));
        if (sameDay >= 2) return ("Already works 12 hours on this day.", null);
        if ((At(-1) && At(-2)) || (At(-1) && At(1)) || (At(1) && At(2))) return ("More than 12 hours in a row.", null);
        return (null, At(-1) || At(1) ? "Back-to-back shift" : null);
    }

    private static void CheckPattern(Dictionary<(int, int), int> cells, int employeeId)
    {
        bool Works(int index)
        {
            var i = ((index % 28) + 28) % 28;
            return cells.TryGetValue((i / DutySlots.Count + 1, i % DutySlots.Count), out var e) && e == employeeId;
        }
        for (var day = 0; day < 7; day++)
            if (Enumerable.Range(0, DutySlots.Count).Count(s => Works(day * DutySlots.Count + s)) > 2)
                throw new BadRequestException("The weekly pattern would give this person more than 12 hours on one day.");
        for (var i = 0; i < 28; i++)
            if (Works(i) && Works(i + 1) && Works(i + 2))
                throw new BadRequestException("The weekly pattern would give this person more than 12 hours in a row.");
    }

    public async Task<IReadOnlyList<DutyCandidateModel>> CandidatesAsync(int lotId, DateOnly date, int slot, int? excludeEmployeeId)
    {
        await EnsureManagerAsync(lotId);
        CheckSlot(slot);
        var lot = await LotAsync(lotId);
        var schedule = await LoadScheduleAsync(lotId, WeekStart(date).AddDays(-1), WeekStart(date).AddDays(7));
        var people = await PeopleAsync(lotId);
        var leaves = await LeavesAsync(people.Keys, date, date);
        var weekStart = WeekStart(date);
        return people.Values.Where(p => p.IsActive && p.EmployeeId != excludeEmployeeId)
            .Select(p =>
            {
                var (blocked, warning) = Check(schedule, p, date, slot, leaves);
                if (blocked == null && schedule.EmployeeAt(date, slot) == p.EmployeeId) blocked = "Already on this shift.";
                var week = Enumerable.Range(0, 7).Sum(d => Enumerable.Range(0, DutySlots.Count).Count(s => schedule.EmployeeAt(weekStart.AddDays(d), s) == p.EmployeeId));
                return new DutyCandidateModel(p.EmployeeId, p.FullName, p.EmployeeCode, p.EmploymentType, blocked == null, warning, blocked, week);
            })
            .OrderBy(c => !c.Eligible).ThenBy(c => c.EmploymentType == EmploymentTypes.Backup).ThenBy(c => c.ShiftsThisWeek).ThenBy(c => c.FullName)
            .ToList();
    }

    /* Changing a shift --------------------------------------------------- */

    public async Task<DutyWeekModel> SetCellAsync(int lotId, DutyCellInput input)
    {
        await EnsureManagerAsync(lotId);
        CheckSlot(input.Slot);
        if (input.Scope is not (DayScope or FollowingScope)) throw new BadRequestException("Choose this day only or this and following weeks.");
        var reason = input.Reason?.Trim();
        if (reason?.Length > 500) throw new BadRequestException("Reason must be 500 characters or fewer.");
        var lot = await LotAsync(lotId);

        await WriteAsync(async () =>
        {
            if (SlotEnd(lot, input.Date, input.Slot) <= Now) throw new BadRequestException("This shift is over and can no longer change.");
            var schedule = await LoadScheduleAsync(lotId, input.Date.AddDays(-1), input.Date.AddDays(1));
            var current = schedule.At(input.Date, input.Slot);
            var started = SlotStart(lot, input.Date, input.Slot) <= Now;
            if (started && input.Scope == FollowingScope)
                throw new BadRequestException("This shift has already begun: replace the person for this day only.");

            var people = await PeopleAsync(lotId, current.EmployeeId is int c ? [c] : null);
            if (input.EmployeeId is int employeeId)
            {
                if (!people.TryGetValue(employeeId, out var person)) throw new BadRequestException("Choose a Staff member of this lot.");
                if (input.Scope == FollowingScope && person.EmploymentType == EmploymentTypes.Backup)
                    throw new BadRequestException("Backup Staff are not part of the weekly pattern. Choose this day only.");
                // A change for one day is an emergency replacement: backup Staff only (or back to the weekly pattern)
                if (input.Scope == DayScope && person.EmploymentType != EmploymentTypes.Backup && employeeId != current.PatternEmployeeId)
                    throw new BadRequestException("Replace someone for one day with a backup Staff member. Full-time Staff change through the weekly pattern.");
                if (current.EmployeeId != employeeId)
                {
                    var (blocked, _) = Check(schedule, person, input.Date, input.Slot, await LeavesAsync([employeeId], input.Date, input.Date));
                    if (blocked != null) throw new BadRequestException($"{person.FullName}: {blocked}");
                }
            }

            // Taken off a shift they are working: their shift ends now
            if (started && current.EmployeeId is int replaced && replaced != input.EmployeeId)
                await CloseReplacedAttendanceAsync(lotId, input.Date, input.Slot, replaced);
            if (input.Scope == FollowingScope) await ChangePatternAsync(lotId, input.Date, input.Slot, input.EmployeeId);
            else await ChangeDayAsync(lotId, input.Date, input.Slot, input.EmployeeId, current.PatternEmployeeId, reason);

            var when = $"{input.Date:dd/MM/yyyy} {DutySlots.Label(input.Slot)}";
            var following = input.Scope == FollowingScope ? " and the same shift every week after" : "";
            if (current.EmployeeId is int before && before != input.EmployeeId && people.TryGetValue(before, out var removed))
                Notify(removed.UserId, "DutyShiftChanged", "Shift removed", $"You are no longer on the {when} shift{following}.", lotId, input.Date);
            if (input.EmployeeId is int after && after != current.EmployeeId)
            {
                var added = people.TryGetValue(after, out var p) ? p : (await PeopleAsync(lotId, [after]))[after];
                Notify(added.UserId, "DutyShiftChanged", "New shift", $"You are on the {when} shift{following}.", lotId, input.Date);
            }
            await SaveAndPublishAsync(lotId);
            return true;
        });
        return await WeekAsync(lot, input.Date, null);
    }

    private async Task ChangeDayAsync(int lotId, DateOnly date, int slot, int? employeeId, int? patternEmployeeId, string reason)
    {
        var change = await db.DutyOverrides.SingleOrDefaultAsync(o => o.ParkingLotId == lotId && o.WorkDate == date && o.Slot == slot);
        if (employeeId == patternEmployeeId && change?.LeaveRequestId == null)
        {
            // Back to the weekly pattern
            if (change != null) db.DutyOverrides.Remove(change);
            return;
        }
        if (change == null)
        {
            change = new DutyOverride { ParkingLotId = lotId, WorkDate = date, Slot = slot };
            db.DutyOverrides.Add(change);
        }
        change.EmployeeId = employeeId;
        change.Reason = reason;
        change.CreatedByUserId = scope.UserId;
        change.CreatedAt = Now;
    }

    /// <summary>Applies the change to the pattern from this date on: a new version starts on the date
    /// (copying the one in force), and versions that start later get the same change.</summary>
    private async Task ChangePatternAsync(int lotId, DateOnly date, int slot, int? employeeId)
    {
        var patterns = await db.DutyPatterns.Where(p => p.ParkingLotId == lotId).OrderBy(p => p.EffectiveFrom).ToListAsync();
        var inForce = patterns.LastOrDefault(p => p.EffectiveFrom <= date);
        if (inForce?.EffectiveFrom != date)
        {
            var copy = new DutyPattern { ParkingLotId = lotId, EffectiveFrom = date, CreatedByUserId = scope.UserId, CreatedAt = Now };
            db.DutyPatterns.Add(copy);
            await db.SaveChangesAsync();
            if (inForce != null)
            {
                var cells = await db.DutyPatternCells.AsNoTracking().Where(c => c.DutyPatternId == inForce.Id).ToListAsync();
                db.DutyPatternCells.AddRange(cells.Select(c => new DutyPatternCell { DutyPatternId = copy.Id, DayOfWeek = c.DayOfWeek, Slot = c.Slot, EmployeeId = c.EmployeeId }));
                await db.SaveChangesAsync();
            }
            patterns.Add(copy);
        }

        var day = IsoDay(date);
        foreach (var pattern in patterns.Where(p => p.EffectiveFrom >= date))
        {
            var cells = await db.DutyPatternCells.Where(c => c.DutyPatternId == pattern.Id).ToListAsync();
            var cell = cells.SingleOrDefault(c => c.DayOfWeek == day && c.Slot == slot);
            if (employeeId == null) { if (cell != null) db.DutyPatternCells.Remove(cell); continue; }
            if (cell == null) { cell = new DutyPatternCell { DutyPatternId = pattern.Id, DayOfWeek = day, Slot = slot }; db.DutyPatternCells.Add(cell); }
            cell.EmployeeId = employeeId.Value;
            var map = cells.Where(c => c != cell).ToDictionary(c => (c.DayOfWeek, c.Slot), c => c.EmployeeId);
            map[(day, slot)] = employeeId.Value;
            CheckPattern(map, employeeId.Value);
        }

        // The pattern now decides this date; one-day changes made by hand on it are dropped (leave ones stay)
        var change = await db.DutyOverrides.SingleOrDefaultAsync(o => o.ParkingLotId == lotId && o.WorkDate == date && o.Slot == slot);
        if (change != null && change.LeaveRequestId == null) db.DutyOverrides.Remove(change);
    }

    /* Saving and notifying ---------------------------------------------- */

    private void Notify(string userId, string type, string title, string message, int lotId, DateOnly? date = null, string refType = "DutySchedule", string refId = null)
    {
        if (string.IsNullOrEmpty(userId)) return;
        _notices.Add(new UserNotification
        {
            UserId = userId, Type = type, Title = title, Message = message, RefType = refType,
            RefId = refId ?? $"{lotId}:{date:yyyy-MM-dd}:{Guid.NewGuid():N}", EventAt = Now, CreatedAt = Now,
            DataJson = System.Text.Json.JsonSerializer.Serialize(new { lotId, date = date?.ToString("yyyy-MM-dd") }),
        });
    }

    private async Task SaveAndPublishAsync(int lotId)
    {
        db.UserNotifications.AddRange(_notices);
        await db.SaveChangesAsync();
        _publish.Add((lotId, _notices.Select(n => n.Id).ToArray()));
        _notices.Clear();
    }

    /// <summary>Pushes saved notices and the lot's refresh signal once the transaction is committed.</summary>
    private void PublishCommitted()
    {
        foreach (var (lotId, ids) in _publish)
        {
            try { jobs?.Enqueue<DutyJobs>(j => j.PublishAsync(lotId, ids)); }
            catch (Exception) { /* the notices are saved; the bell and the board poll them */ }
        }
        _publish.Clear();
    }

    /// <summary>Serializes duty writers across API processes (SQL application lock inside the transaction).</summary>
    private async Task<T> WriteAsync<T>(Func<Task<T>> operation)
    {
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable) : null;
        if (db.Database.IsSqlServer())
            await db.Database.ExecuteSqlRawAsync("""
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource = N'PMS.DutySchedule', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
                IF @result < 0 THROW 51000, 'The duty board is busy; please retry.', 1;
                """);
        try
        {
            var result = await operation();
            if (tx != null) await tx.CommitAsync();
            PublishCommitted();
            return result;
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
        {
            throw new ConflictException("Someone changed this at the same time. Refresh and try again.");
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("Someone changed this at the same time. Refresh and try again.");
        }
        finally { _notices.Clear(); _publish.Clear(); }
    }
}
