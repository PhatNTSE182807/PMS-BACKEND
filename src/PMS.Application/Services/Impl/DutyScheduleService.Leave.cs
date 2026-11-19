using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.DutySchedule;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Leave: a Staff member asks for a range of dates with a reason; the lot's Manager approves (choosing who
/// takes each shift, or leaving it empty) or rejects. At most 7 calendar days a month over all requests,
/// none in the month someone joined (unless they joined on the 1st), 12 paid days a year by default.
/// </summary>
public partial class DutyScheduleService
{
    private const string Pending = "Pending";
    private const string Approved = "Approved";

    private static IEnumerable<DateOnly> Days(DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to; d = d.AddDays(1)) yield return d;
    }

    private static int DaysInMonth(DateOnly from, DateOnly to, int year, int month) =>
        Days(from, to).Count(d => d.Year == year && d.Month == month);

    /// <summary>First date someone may take leave: the 1st of the month after they joined, unless they joined on a 1st.</summary>
    private static DateOnly? NoLeaveUntil(Person person) =>
        person.HireDate is DateOnly hired && hired.Day != 1 ? new DateOnly(hired.Year, hired.Month, 1).AddMonths(1) : null;

    private async Task<int> EntitledDaysAsync(string userId, int year) =>
        await db.WorkLeaveBalances.AsNoTracking().Where(b => b.UserId == userId && b.Year == year)
            .Select(b => (int?)b.EntitledDays).FirstOrDefaultAsync() ?? options.AnnualPaidLeaveDays;

    private Task<int> PaidDaysUsedAsync(int employeeId, int year, int? exceptRequestId = null) =>
        (from day in db.DutyLeaveDays
         join request in db.DutyLeaveRequests on day.DutyLeaveRequestId equals request.Id
         where day.EmployeeId == employeeId && day.IsPaid && day.LeaveDate.Year == year && request.Status == Approved && request.Id != exceptRequestId
         select day.Id).CountAsync();

    /// <summary>The person's shifts in the range that someone else must take (not already started).</summary>
    private async Task<List<LeaveShiftModel>> LeaveShiftsAsync(LotInfo lot, int employeeId, DateOnly from, DateOnly to)
    {
        var schedule = await LoadScheduleAsync(lot.Id, from, to);
        var started = await db.DutyShiftAttendances.AsNoTracking()
            .Where(a => a.EmployeeId == employeeId && a.WorkDate >= from && a.WorkDate <= to && a.CheckInAt != null)
            .Select(a => new { a.WorkDate, a.Slot }).ToListAsync();
        return schedule.Cells.Where(c => c.Value.EmployeeId == employeeId && !started.Any(s => s.WorkDate == c.Key.Item1 && s.Slot == c.Key.Item2))
            .OrderBy(c => c.Key.Item1).ThenBy(c => c.Key.Item2)
            .Select(c => new LeaveShiftModel(c.Key.Item1, c.Key.Item2, SlotStart(lot, c.Key.Item1, c.Key.Item2), SlotEnd(lot, c.Key.Item1, c.Key.Item2)))
            .ToList();
    }

    /// <summary>Paid / unpaid split of the days with a shift, taking paid days while the yearly allowance lasts.</summary>
    private async Task<List<(DateOnly Date, bool HadShift, bool IsPaid)>> SplitPaidAsync(Person person, DateOnly from, DateOnly to,
        IReadOnlyCollection<LeaveShiftModel> shifts, int? exceptRequestId = null)
    {
        var shiftDays = shifts.Select(s => s.Date).ToHashSet();
        var remaining = new Dictionary<int, int>();
        var result = new List<(DateOnly, bool, bool)>();
        foreach (var day in Days(from, to))
        {
            if (!remaining.ContainsKey(day.Year))
                remaining[day.Year] = await EntitledDaysAsync(person.UserId, day.Year) - await PaidDaysUsedAsync(person.EmployeeId, day.Year, exceptRequestId);
            var hadShift = shiftDays.Contains(day);
            var paid = hadShift && remaining[day.Year] > 0;
            if (paid) remaining[day.Year]--;
            result.Add((day, hadShift, paid));
        }
        return result;
    }

    /// <summary>Why the request cannot be sent, or null.</summary>
    private async Task<string> LeaveProblemAsync(Person me, LotInfo lot, DateOnly from, DateOnly to, int? ignoreRequestId = null)
    {
        if (to < from) return "The end date cannot be before the start date.";
        if (from < Today(lot)) return "Leave cannot start in the past.";
        if (to > Today(lot).AddDays(180)) return "Leave can be requested at most 6 months ahead.";
        if (NoLeaveUntil(me) is DateOnly until && from < until) return "Leave is not available in your first month.";

        var mine = await db.DutyLeaveRequests.AsNoTracking()
            .Where(r => r.EmployeeId == me.EmployeeId && r.Id != ignoreRequestId && (r.Status == Pending || r.Status == Approved)).ToListAsync();
        if (mine.Any(r => r.FromDate <= to && r.ToDate >= from)) return "You already asked for leave on some of these days.";
        foreach (var month in Days(from, to).Select(d => (d.Year, d.Month)).Distinct())
        {
            var used = mine.Sum(r => DaysInMonth(r.FromDate, r.ToDate, month.Year, month.Month));
            var asked = DaysInMonth(from, to, month.Year, month.Month);
            if (used + asked > options.MaxLeaveDaysPerMonth)
                return $"Leave limit reached: {options.MaxLeaveDaysPerMonth} days per month ({used} already used in {month.Month:00}/{month.Year}).";
        }
        if (await db.DutyShiftAttendances.AnyAsync(a => a.EmployeeId == me.EmployeeId && a.WorkDate >= from && a.WorkDate <= to && a.CheckInAt != null))
            return "You have already started a shift in these days.";
        return null;
    }

    /* Staff ------------------------------------------------------------- */

    public async Task<LeavePreviewModel> PreviewLeaveAsync(DateOnly from, DateOnly to)
    {
        var me = await CurrentStaffAsync();
        var lot = await LotAsync(me.ParkingLotId!.Value);
        var problem = await LeaveProblemAsync(me, lot, from, to);
        if (to < from) return new LeavePreviewModel { FromDate = from, ToDate = to, Problem = problem };
        var shifts = await LeaveShiftsAsync(lot, me.EmployeeId, from, to);
        var split = await SplitPaidAsync(me, from, to, shifts);
        return new LeavePreviewModel
        {
            FromDate = from, ToDate = to, CalendarDays = to.DayNumber - from.DayNumber + 1, Shifts = shifts,
            PaidDays = split.Count(d => d.IsPaid), UnpaidDays = split.Count(d => d.HadShift && !d.IsPaid), Problem = problem,
        };
    }

    public async Task<MyLeaveModel> MyLeaveAsync()
    {
        var me = await CurrentStaffAsync();
        var lot = await LotAsync(me.ParkingLotId!.Value);
        var today = Today(lot);
        var requests = await db.DutyLeaveRequests.AsNoTracking().Where(r => r.EmployeeId == me.EmployeeId)
            .OrderByDescending(r => r.FromDate).Take(50).ToListAsync();
        return new MyLeaveModel
        {
            Balance = await BalanceAsync(me, today),
            Requests = await LeaveModelsAsync(lot, requests, new Dictionary<int, Person> { [me.EmployeeId] = me }),
            NoLeaveUntil = NoLeaveUntil(me) is DateOnly until && until > today ? until : null,
        };
    }

    private async Task<LeaveBalanceModel> BalanceAsync(Person me, DateOnly today)
    {
        var thisMonth = await db.DutyLeaveRequests.AsNoTracking()
            .Where(r => r.EmployeeId == me.EmployeeId && (r.Status == Pending || r.Status == Approved)
                && r.FromDate.Year * 12 + r.FromDate.Month <= today.Year * 12 + today.Month && r.ToDate.Year * 12 + r.ToDate.Month >= today.Year * 12 + today.Month)
            .ToListAsync();
        return new LeaveBalanceModel
        {
            Year = today.Year, EntitledDays = await EntitledDaysAsync(me.UserId, today.Year),
            PaidDaysUsed = await PaidDaysUsedAsync(me.EmployeeId, today.Year),
            MaxDaysPerMonth = options.MaxLeaveDaysPerMonth,
            DaysThisMonth = thisMonth.Sum(r => DaysInMonth(r.FromDate, r.ToDate, today.Year, today.Month)),
        };
    }

    public async Task<LeaveRequestModel> RequestLeaveAsync(LeaveRequestInput input)
    {
        var me = await CurrentStaffAsync();
        var lot = await LotAsync(me.ParkingLotId!.Value);
        var reason = input.Reason?.Trim();
        if (string.IsNullOrEmpty(reason)) throw new BadRequestException("Give a reason for your leave.");
        if (reason.Length > 500) throw new BadRequestException("Reason must be 500 characters or fewer.");
        DutyLeaveRequest request = null;
        await WriteAsync(async () =>
        {
            var problem = await LeaveProblemAsync(me, lot, input.FromDate, input.ToDate);
            if (problem != null) throw new BadRequestException(problem);
            request = new DutyLeaveRequest
            {
                EmployeeId = me.EmployeeId, ParkingLotId = lot.Id, FromDate = input.FromDate, ToDate = input.ToDate,
                Reason = reason, Status = Pending, CreatedAt = Now,
            };
            db.DutyLeaveRequests.Add(request);
            await db.SaveChangesAsync();
            var range = input.FromDate == input.ToDate ? $"{input.FromDate:dd/MM/yyyy}" : $"{input.FromDate:dd/MM} – {input.ToDate:dd/MM/yyyy}";
            Notify(lot.ManagerUserId, "DutyLeaveRequested", "Leave request", $"{me.FullName} asked for leave {range}.", lot.Id, input.FromDate,
                "DutyLeaveRequest", request.Id.ToString());
            await SaveAndPublishAsync(lot.Id);
            return true;
        });
        return (await LeaveModelsAsync(lot, [request], new Dictionary<int, Person> { [me.EmployeeId] = me }))[0];
    }

    public async Task<MyLeaveModel> CancelLeaveAsync(int requestId)
    {
        var me = await CurrentStaffAsync();
        await WriteAsync(async () =>
        {
            var request = await db.DutyLeaveRequests.SingleOrDefaultAsync(r => r.Id == requestId && r.EmployeeId == me.EmployeeId)
                ?? throw new NotFoundException("Leave request not found.");
            if (request.Status != Pending) throw new BadRequestException("Only a waiting request can be cancelled.");
            request.Status = "Cancelled";
            request.DecidedAt = Now;
            request.DecidedByUserId = me.UserId;
            await SaveAndPublishAsync(request.ParkingLotId);
            return true;
        });
        return await MyLeaveAsync();
    }

    /* Manager ----------------------------------------------------------- */

    public async Task<IReadOnlyList<LeaveRequestModel>> LotLeaveAsync(int lotId)
    {
        await EnsureManagerAsync(lotId);
        var lot = await LotAsync(lotId);
        var pending = await db.DutyLeaveRequests.AsNoTracking().Where(r => r.ParkingLotId == lotId && r.Status == Pending)
            .OrderBy(r => r.FromDate).ToListAsync();
        var others = await db.DutyLeaveRequests.AsNoTracking().Where(r => r.ParkingLotId == lotId && r.Status != Pending)
            .OrderByDescending(r => r.DecidedAt ?? r.CreatedAt).Take(100).ToListAsync();
        var all = pending.Concat(others).ToList();
        var people = await PeopleAsync(lotId, all.Select(r => r.EmployeeId));
        return await LeaveModelsAsync(lot, all, people);
    }

    private async Task<List<LeaveRequestModel>> LeaveModelsAsync(LotInfo lot, List<DutyLeaveRequest> requests, IReadOnlyDictionary<int, Person> people)
    {
        var ids = requests.Select(r => r.Id).ToList();
        var days = await db.DutyLeaveDays.AsNoTracking().Where(d => ids.Contains(d.DutyLeaveRequestId)).ToListAsync();
        var result = new List<LeaveRequestModel>();
        foreach (var r in requests)
        {
            people.TryGetValue(r.EmployeeId, out var person);
            IReadOnlyList<LeaveShiftModel> shifts = [];
            int paid, unpaid;
            if (r.Status == Pending && person != null)
            {
                shifts = await LeaveShiftsAsync(lot, r.EmployeeId, r.FromDate, r.ToDate);
                var split = await SplitPaidAsync(person, r.FromDate, r.ToDate, shifts);
                paid = split.Count(d => d.IsPaid);
                unpaid = split.Count(d => d.HadShift && !d.IsPaid);
            }
            else
            {
                var mine = days.Where(d => d.DutyLeaveRequestId == r.Id).ToList();
                paid = mine.Count(d => d.IsPaid);
                unpaid = mine.Count(d => d.HadShift && !d.IsPaid);
            }
            result.Add(new LeaveRequestModel
            {
                Id = r.Id, EmployeeId = r.EmployeeId, EmployeeName = person?.FullName, EmploymentType = person?.EmploymentType,
                FromDate = r.FromDate, ToDate = r.ToDate, Reason = r.Reason, Status = r.Status, DecisionReason = r.DecisionReason,
                CreatedAt = r.CreatedAt, DecidedAt = r.DecidedAt, PaidDays = paid, UnpaidDays = unpaid, Shifts = shifts,
            });
        }
        return result;
    }

    public async Task<IReadOnlyList<LeaveRequestModel>> ApproveLeaveAsync(int lotId, int requestId, LeaveDecisionInput input)
    {
        await EnsureManagerAsync(lotId);
        var lot = await LotAsync(lotId);
        await WriteAsync(async () =>
        {
            var request = await db.DutyLeaveRequests.SingleOrDefaultAsync(r => r.Id == requestId && r.ParkingLotId == lotId)
                ?? throw new NotFoundException("Leave request not found.");
            if (request.Status != Pending) throw new BadRequestException("This request has already been decided.");
            var people = await PeopleAsync(lotId, [request.EmployeeId]);
            var person = people.TryGetValue(request.EmployeeId, out var p) ? p : throw new BadRequestException("This Staff member is no longer at this lot.");

            var shifts = await LeaveShiftsAsync(lot, request.EmployeeId, request.FromDate, request.ToDate);
            var schedule = await LoadScheduleAsync(lotId, request.FromDate.AddDays(-1), request.ToDate.AddDays(1));
            // The person on leave no longer counts on these shifts
            foreach (var s in shifts) schedule.Cells[(s.Date, s.Slot)] = schedule.At(s.Date, s.Slot) with { EmployeeId = null };
            var leaves = await LeavesAsync(people.Keys, request.FromDate, request.ToDate);
            var chosen = new Dictionary<(DateOnly, int), int>();
            foreach (var r in input.Replacements ?? [])
            {
                if (r.EmployeeId is not int employeeId) continue;
                if (!shifts.Any(s => s.Date == r.Date && s.Slot == r.Slot)) throw new BadRequestException("A replacement was chosen for a shift that is not part of this leave.");
                if (employeeId == request.EmployeeId) throw new BadRequestException("The person on leave cannot replace themselves.");
                if (!people.TryGetValue(employeeId, out var replacement)) throw new BadRequestException("Choose replacements among this lot's Staff.");
                var (blocked, _) = Check(schedule, replacement, r.Date, r.Slot, leaves);
                if (blocked != null) throw new BadRequestException($"{replacement.FullName} on {r.Date:dd/MM} {DutySlots.Label(r.Slot)}: {blocked}");
                schedule.Cells[(r.Date, r.Slot)] = schedule.At(r.Date, r.Slot) with { EmployeeId = employeeId };
                chosen[(r.Date, r.Slot)] = employeeId;
            }

            foreach (var day in await SplitPaidAsync(person, request.FromDate, request.ToDate, shifts))
                db.DutyLeaveDays.Add(new DutyLeaveDay { DutyLeaveRequestId = request.Id, EmployeeId = request.EmployeeId, LeaveDate = day.Date, HadShift = day.HadShift, IsPaid = day.IsPaid });

            var existing = await db.DutyOverrides.Where(o => o.ParkingLotId == lotId && o.WorkDate >= request.FromDate && o.WorkDate <= request.ToDate).ToListAsync();
            foreach (var s in shifts)
            {
                var change = existing.SingleOrDefault(o => o.WorkDate == s.Date && o.Slot == s.Slot);
                if (change == null) { change = new DutyOverride { ParkingLotId = lotId, WorkDate = s.Date, Slot = s.Slot }; db.DutyOverrides.Add(change); }
                change.EmployeeId = chosen.TryGetValue((s.Date, s.Slot), out var e) ? e : null;
                change.LeaveRequestId = request.Id;
                change.Reason = "Leave";
                change.CreatedByUserId = scope.UserId;
                change.CreatedAt = Now;
            }

            request.Status = Approved;
            request.DecisionReason = input.Reason?.Trim() is { Length: > 0 } note ? note[..Math.Min(note.Length, 500)] : null;
            request.DecidedByUserId = scope.UserId;
            request.DecidedAt = Now;

            var range = request.FromDate == request.ToDate ? $"{request.FromDate:dd/MM/yyyy}" : $"{request.FromDate:dd/MM} – {request.ToDate:dd/MM/yyyy}";
            Notify(person.UserId, "DutyLeaveDecided", "Leave approved", $"Your leave {range} is approved.", lotId, request.FromDate, "DutyLeaveRequest", request.Id.ToString());
            var replacementPeople = await PeopleAsync(lotId, chosen.Values);
            foreach (var group in chosen.GroupBy(c => c.Value))
                if (replacementPeople.TryGetValue(group.Key, out var who))
                    Notify(who.UserId, "DutyShiftChanged", "New shift",
                        $"You cover {person.FullName}: {string.Join(", ", group.Select(g => $"{g.Key.Item1:dd/MM} {DutySlots.Label(g.Key.Item2)}"))}.", lotId, group.First().Key.Item1);
            await SaveAndPublishAsync(lotId);
            return true;
        });
        return await LotLeaveAsync(lotId);
    }

    public async Task<IReadOnlyList<LeaveRequestModel>> RejectLeaveAsync(int lotId, int requestId, LeaveDecisionInput input)
    {
        await EnsureManagerAsync(lotId);
        var reason = input.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 500) throw new BadRequestException("Give a reason of at most 500 characters.");
        await WriteAsync(async () =>
        {
            var request = await db.DutyLeaveRequests.SingleOrDefaultAsync(r => r.Id == requestId && r.ParkingLotId == lotId)
                ?? throw new NotFoundException("Leave request not found.");
            if (request.Status != Pending) throw new BadRequestException("This request has already been decided.");
            request.Status = "Rejected";
            request.DecisionReason = reason;
            request.DecidedByUserId = scope.UserId;
            request.DecidedAt = Now;
            var userId = await db.Employees.Where(e => e.Id == request.EmployeeId).Select(e => e.UserId).SingleAsync();
            var range = request.FromDate == request.ToDate ? $"{request.FromDate:dd/MM/yyyy}" : $"{request.FromDate:dd/MM} – {request.ToDate:dd/MM/yyyy}";
            Notify(userId, "DutyLeaveDecided", "Leave rejected", $"Your leave {range} was rejected: {reason}", lotId, request.FromDate, "DutyLeaveRequest", request.Id.ToString());
            await SaveAndPublishAsync(lotId);
            return true;
        });
        return await LotLeaveAsync(lotId);
    }

    /* Admin: yearly paid leave ----------------------------------------- */

    public async Task<IReadOnlyList<LeaveBalanceRowModel>> LeaveBalancesAsync(int year)
    {
        if (scope.Role != "Admin") throw new ForbiddenException("Only an Admin sets leave allowances.");
        var staffIds = await identity.Users.AsNoTracking().Where(u => u.Role == StaffRole).Select(u => u.Id).ToListAsync();
        var employees = await db.Employees.AsNoTracking().Where(e => staffIds.Contains(e.UserId) && e.Status != "Terminated").ToListAsync();
        var lots = await db.ParkingLots.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name);
        var userIds = employees.Select(e => e.UserId).ToList();
        var balances = await db.WorkLeaveBalances.AsNoTracking().Where(b => b.Year == year && userIds.Contains(b.UserId)).ToDictionaryAsync(b => b.UserId);
        var result = new List<LeaveBalanceRowModel>();
        foreach (var e in employees.OrderBy(e => e.FullName))
        {
            balances.TryGetValue(e.UserId, out var b);
            result.Add(new LeaveBalanceRowModel(e.Id, e.UserId, e.FullName, e.EmployeeCode,
                e.ParkingLotId is int lotId && lots.TryGetValue(lotId, out var name) ? name : null,
                EmploymentTypes.IsValid(e.EmploymentType) ? e.EmploymentType : EmploymentTypes.FullTime, year,
                b?.EntitledDays ?? options.AnnualPaidLeaveDays, await PaidDaysUsedAsync(e.Id, year), b?.Revision ?? 0));
        }
        return result;
    }

    public async Task<IReadOnlyList<LeaveBalanceRowModel>> SetLeaveBalanceAsync(int employeeId, int year, LeaveBalanceInput input)
    {
        if (scope.Role != "Admin") throw new ForbiddenException("Only an Admin sets leave allowances.");
        if (input.EntitledDays is < 0 or > 365) throw new BadRequestException("Paid leave days must be between 0 and 365.");
        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == employeeId) ?? throw new NotFoundException("Staff member not found.");
        if (input.EntitledDays < await PaidDaysUsedAsync(employeeId, year))
            throw new BadRequestException("The allowance cannot be lower than the paid days already taken.");
        var balance = await db.WorkLeaveBalances.SingleOrDefaultAsync(b => b.UserId == employee.UserId && b.Year == year);
        if (balance == null)
        {
            if (input.Revision != 0) throw new ConflictException("This allowance changed. Refresh and try again.");
            db.WorkLeaveBalances.Add(new WorkLeaveBalance { UserId = employee.UserId, Year = year, EntitledDays = input.EntitledDays, Revision = 1 });
        }
        else
        {
            if (balance.Revision != input.Revision) throw new ConflictException("This allowance changed. Refresh and try again.");
            balance.EntitledDays = input.EntitledDays;
            balance.Revision++;
        }
        await db.SaveChangesAsync();
        return await LeaveBalancesAsync(year);
    }
}
