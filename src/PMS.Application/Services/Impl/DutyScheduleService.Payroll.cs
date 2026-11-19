using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.DutySchedule;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Monthly pay: full-time Staff 5,800,000 and Managers 10,000,000 a month (the first month by the days
/// from the hire date), minus salary / 26 per day absent or on unpaid leave; full-time Staff earn
/// 30,000 an hour for shifts beyond their weekly pattern and backup Staff 30,000 per hour worked.
/// An Admin pays a finished month with one action; Staff and Managers can also simulate a payday.
/// </summary>
public partial class DutyScheduleService
{
    private const string Unpaid = "Unpaid";
    private const string Paid = "Paid";
    private static readonly JsonSerializerOptions LineJson = new(JsonSerializerDefaults.Web);
    private static readonly TimeZoneInfo PayZone = ParkingText.FindTimeZone("SE Asia Standard Time");

    private DateOnly PayToday => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Now, PayZone));

    private static DateOnly MonthOf(DateOnly date) => new(date.Year, date.Month, 1);

    private static decimal Round(decimal amount) => Math.Round(amount, 0, MidpointRounding.AwayFromZero);

    private bool MonthEnded(DateOnly month) => month.AddMonths(1) <= PayToday;

    private void EnsureAdmin()
    {
        if (scope.Role != "Admin") throw new ForbiddenException("Only an Admin manages payroll.");
    }

    private sealed record Payee(Employee Employee, string Kind, int? LotId);

    /// <summary>Staff and Managers employed during the month.</summary>
    private async Task<List<Payee>> PayeesAsync(DateOnly month, int? employeeId = null)
    {
        var monthEnd = month.AddMonths(1).AddDays(-1);
        var roles = await identity.Users.AsNoTracking().Where(u => u.Role == StaffRole || u.Role == "Manager")
            .Select(u => new { u.Id, u.Role }).ToDictionaryAsync(u => u.Id, u => u.Role);
        var userIds = roles.Keys.ToList();
        var employees = await db.Employees.AsNoTracking()
            .Where(e => userIds.Contains(e.UserId) && (employeeId == null || e.Id == employeeId)
                && (e.HireDate == null || e.HireDate <= monthEnd)
                && (e.Status == ActiveStatus || (e.Status == "Terminated" && e.TerminationDate != null && e.TerminationDate >= month)))
            .ToListAsync();
        var managedLots = await db.ParkingLots.AsNoTracking().Where(l => !l.IsDeleted && l.ManagerUserId != null)
            .GroupBy(l => l.ManagerUserId).Select(g => new { g.Key, LotId = g.Min(l => l.Id) }).ToDictionaryAsync(g => g.Key, g => g.LotId);
        return employees.Select(e => roles[e.UserId] == "Manager"
                ? new Payee(e, EmploymentTypes.Manager, managedLots.TryGetValue(e.UserId, out var lotId) ? lotId : null)
                : new Payee(e, EmploymentTypes.IsValid(e.EmploymentType) ? e.EmploymentType : EmploymentTypes.FullTime, e.ParkingLotId))
            .ToList();
    }

    /// <summary>Paid minutes of a shift: check-in to check-out, inside the shift's hours.</summary>
    private static int PaidMinutes(LotInfo lot, DutyShiftAttendance a)
    {
        if (a.CheckInAt is not DateTime inAt || a.CheckOutAt is not DateTime outAt) return 0;
        var start = SlotStart(lot, a.WorkDate, a.Slot);
        var end = SlotEnd(lot, a.WorkDate, a.Slot);
        var from = inAt > start ? inAt : start;
        var to = outAt < end ? outAt : end;
        return to > from ? (int)Math.Floor((to - from).TotalMinutes) : 0;
    }


    private async Task<PaySlip> CalculateAsync(Payee payee, DateOnly month)
    {
        var e = payee.Employee;
        var monthEnd = month.AddMonths(1).AddDays(-1);
        var lines = new List<PayLineModel>();
        decimal baseAmount = 0, overtime = 0;

        if (payee.Kind is EmploymentTypes.FullTime or EmploymentTypes.Manager)
        {
            var monthly = payee.Kind == EmploymentTypes.Manager ? pay.ManagerMonthly : pay.FullTimeStaffMonthly;
            baseAmount = monthly;
            var label = "Base salary";
            if (e.HireDate is DateOnly hired && hired >= month && hired <= monthEnd && hired.Day > 1)
            {
                var days = monthEnd.Day - hired.Day + 1;
                baseAmount = Round(monthly * days / monthEnd.Day);
                label = $"Base salary from {hired:dd/MM} ({days} of {monthEnd.Day} days)";
            }
            lines.Add(new PayLineModel { Kind = "Base", Label = label, Quantity = 1, UnitAmount = baseAmount, Amount = baseAmount });
        }

        var records = await db.DutyShiftAttendances.AsNoTracking()
            .Where(a => a.EmployeeId == e.Id && a.WorkDate >= month && a.WorkDate <= monthEnd).ToListAsync();
        var lots = new Dictionary<int, LotInfo>();
        foreach (var lotId in records.Select(r => r.ParkingLotId).Distinct()) lots[lotId] = await LotAsync(lotId);

        if (payee.Kind == EmploymentTypes.FullTime)
        {
            var daily = Round(pay.FullTimeStaffMonthly / pay.StandardDays);
            var absent = records.Where(r => r.Status == Absent).Select(r => r.WorkDate).Distinct().OrderBy(d => d).ToList();
            if (absent.Count > 0)
                lines.Add(new PayLineModel { Kind = "Absence", Label = $"Absent {absent.Count} day(s)", Quantity = absent.Count, UnitAmount = -daily,
                    Amount = -daily * absent.Count, Details = absent.Select(d => d.ToString("yyyy-MM-dd")).ToList() });

            var unpaid = await (from day in db.DutyLeaveDays
                                join request in db.DutyLeaveRequests on day.DutyLeaveRequestId equals request.Id
                                where day.EmployeeId == e.Id && request.Status == Approved && day.HadShift && !day.IsPaid
                                    && day.LeaveDate >= month && day.LeaveDate <= monthEnd
                                select day.LeaveDate).Distinct().ToListAsync();
            unpaid = unpaid.Except(absent).OrderBy(d => d).ToList();
            if (unpaid.Count > 0)
                lines.Add(new PayLineModel { Kind = "UnpaidLeave", Label = $"Unpaid leave {unpaid.Count} day(s)", Quantity = unpaid.Count, UnitAmount = -daily,
                    Amount = -daily * unpaid.Count, Details = unpaid.Select(d => d.ToString("yyyy-MM-dd")).ToList() });

            // Shifts beyond the person's weekly pattern on a day are extra (a second shift, or a shift on a day off)
            var extra = new List<DutyShiftAttendance>();
            foreach (var group in records.Where(r => r.CheckInAt != null && r.CheckOutAt != null).GroupBy(r => (r.ParkingLotId, r.WorkDate)))
            {
                var schedule = await LoadScheduleAsync(group.Key.ParkingLotId, group.Key.WorkDate, group.Key.WorkDate);
                var planned = schedule.PatternSlots(e.Id, group.Key.WorkDate).ToHashSet();
                var count = Math.Max(0, group.Count() - planned.Count);
                extra.AddRange(group.OrderBy(r => planned.Contains(r.Slot)).ThenByDescending(r => r.Slot).Take(count));
            }
            overtime = AddHours(lines, "ExtraHours", "Extra shifts", extra, lots);
        }
        else if (payee.Kind == EmploymentTypes.Backup)
        {
            overtime = AddHours(lines, "Hours", "Hours worked", records.Where(r => r.CheckInAt != null && r.CheckOutAt != null).ToList(), lots);
        }

        var adjustments = await db.PayAdjustments.AsNoTracking().Where(a => a.EmployeeId == e.Id && a.Month == month).OrderBy(a => a.CreatedAt).ToListAsync();
        foreach (var a in adjustments)
            lines.Add(new PayLineModel { Kind = "Adjustment", Label = $"Adjustment: {a.Reason}", Quantity = 1, UnitAmount = a.Amount, Amount = a.Amount });

        var deductions = -lines.Where(l => l.Kind is "Absence" or "UnpaidLeave").Sum(l => l.Amount);
        var adjustTotal = adjustments.Sum(a => a.Amount);
        return new PaySlip
        {
            EmployeeId = e.Id, UserId = e.UserId, ParkingLotId = payee.LotId, Month = month, Kind = payee.Kind,
            BaseAmount = baseAmount, Deductions = deductions, Overtime = overtime, Adjustments = adjustTotal,
            NetAmount = baseAmount - deductions + overtime + adjustTotal, Status = Unpaid,
            LinesJson = JsonSerializer.Serialize(lines, LineJson),
        };
    }

    private static bool IsEmpty(PaySlip slip) => slip.NetAmount == 0 && slip.BaseAmount == 0 && slip.Adjustments == 0 && slip.Overtime == 0;

    private decimal AddHours(List<PayLineModel> lines, string kind, string label, List<DutyShiftAttendance> shifts, Dictionary<int, LotInfo> lots)
    {
        // Paid in whole hours: each shift counts the full hours worked inside it
        int Hours(DutyShiftAttendance s) => PaidMinutes(lots[s.ParkingLotId], s) / 60;
        var hours = shifts.Sum(Hours);
        if (hours == 0) return 0;
        var amount = hours * pay.HourlyRate;
        lines.Add(new PayLineModel
        {
            Kind = kind, Label = $"{label} ({hours}h)", Quantity = hours, UnitAmount = pay.HourlyRate, Amount = amount,
            Details = shifts.Where(s => Hours(s) > 0).OrderBy(s => s.WorkDate).ThenBy(s => s.Slot)
                .Select(s => $"{s.WorkDate:yyyy-MM-dd} {DutySlots.Label(s.Slot)} ({Hours(s)}h)").ToList(),
        });
        return amount;
    }

    /// <summary>Creates or refreshes the unpaid payslips of a finished month (paid ones never change).</summary>
    private async Task EnsureMonthSlipsAsync(DateOnly month)
    {
        if (!MonthEnded(month)) return;
        await WriteAsync(async () =>
        {
            var existing = await db.PaySlips.Where(p => p.Month == month && !p.IsSimulated).ToDictionaryAsync(p => p.EmployeeId);
            foreach (var payee in await PayeesAsync(month))
            {
                existing.TryGetValue(payee.Employee.Id, out var slip);
                if (slip?.Status == Paid) continue;
                var fresh = await CalculateAsync(payee, month);
                if (IsEmpty(fresh))
                {
                    // A backup with no hours (and no adjustment) has nothing to be paid
                    if (slip != null) db.PaySlips.Remove(slip);
                    continue;
                }
                if (slip == null)
                {
                    fresh.CreatedAt = fresh.UpdatedAt = Now;
                    db.PaySlips.Add(fresh);
                    continue;
                }
                if (slip.LinesJson == fresh.LinesJson && slip.NetAmount == fresh.NetAmount && slip.ParkingLotId == fresh.ParkingLotId) continue;
                slip.Kind = fresh.Kind; slip.ParkingLotId = fresh.ParkingLotId;
                slip.BaseAmount = fresh.BaseAmount; slip.Deductions = fresh.Deductions; slip.Overtime = fresh.Overtime;
                slip.Adjustments = fresh.Adjustments; slip.NetAmount = fresh.NetAmount; slip.LinesJson = fresh.LinesJson;
                slip.UpdatedAt = Now;
            }
            await db.SaveChangesAsync();
            return true;
        });
    }

    private async Task<List<PaySlipModel>> SlipModelsAsync(IEnumerable<PaySlip> slips)
    {
        var list = slips.ToList();
        var employeeIds = list.Select(s => s.EmployeeId).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking().Where(e => employeeIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => new { e.FullName, e.EmployeeCode });
        var lots = await db.ParkingLots.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name);
        return list.Select(s => new PaySlipModel
        {
            Id = s.Id, EmployeeId = s.EmployeeId, EmployeeName = employees.TryGetValue(s.EmployeeId, out var e) ? e.FullName : null,
            EmployeeCode = e?.EmployeeCode, ParkingLotId = s.ParkingLotId,
            LotName = s.ParkingLotId is int lotId && lots.TryGetValue(lotId, out var name) ? name : null,
            Month = s.Month, Kind = s.Kind, BaseAmount = s.BaseAmount, Deductions = s.Deductions, Overtime = s.Overtime,
            Adjustments = s.Adjustments, NetAmount = s.NetAmount, Status = s.Status, IsSimulated = s.IsSimulated, PaidAt = s.PaidAt,
            Lines = string.IsNullOrEmpty(s.LinesJson) ? [] : JsonSerializer.Deserialize<List<PayLineModel>>(s.LinesJson, LineJson) ?? [],
        }).ToList();
    }

    /* Admin ------------------------------------------------------------- */

    public async Task<PayrollReportModel> PayrollReportAsync(DateOnly monthDate, int? lotId, string kind, string status, bool includeSimulated)
    {
        EnsureAdmin();
        var month = MonthOf(monthDate);
        var ended = MonthEnded(month);
        await EnsureMonthSlipsAsync(month);

        List<PaySlip> real;
        if (ended) real = await db.PaySlips.AsNoTracking().Where(p => p.Month == month && !p.IsSimulated).ToListAsync();
        else
        {
            // A month still running: what each person would get so far (nothing is saved)
            real = [];
            foreach (var payee in await PayeesAsync(month))
            {
                var s = await CalculateAsync(payee, month);
                if (IsEmpty(s)) continue;
                s.Status = "Estimate";
                real.Add(s);
            }
        }
        var simulated = includeSimulated
            ? await db.PaySlips.AsNoTracking().Where(p => p.Month == month && p.IsSimulated).ToListAsync()
            : [];

        var rows = real.Concat(simulated)
            .Where(p => lotId == null || p.ParkingLotId == lotId)
            .Where(p => string.IsNullOrEmpty(kind) || p.Kind == kind)
            .Where(p => string.IsNullOrEmpty(status) || p.Status == status)
            .ToList();
        var models = (await SlipModelsAsync(rows))
            .OrderBy(m => m.IsSimulated).ThenBy(m => m.Kind == EmploymentTypes.Manager ? 0 : m.Kind == EmploymentTypes.FullTime ? 1 : 2)
            .ThenBy(m => m.LotName).ThenBy(m => m.EmployeeName).ThenByDescending(m => m.PaidAt).ToList();
        return new PayrollReportModel
        {
            Month = month, MonthEnded = ended,
            UnpaidCount = models.Count(m => !m.IsSimulated && m.Status == Unpaid),
            UnpaidTotal = models.Where(m => !m.IsSimulated && m.Status == Unpaid).Sum(m => m.NetAmount),
            PaidTotal = models.Where(m => !m.IsSimulated && m.Status == Paid).Sum(m => m.NetAmount),
            SimulatedTotal = models.Where(m => m.IsSimulated).Sum(m => m.NetAmount),
            Slips = models,
        };
    }

    public async Task<PayAllResultModel> PayAllAsync(PayAllInput input)
    {
        EnsureAdmin();
        var month = MonthOf(input.Month);
        if (!MonthEnded(month)) throw new BadRequestException("A month can only be paid once it has ended.");
        await EnsureMonthSlipsAsync(month);
        var result = await WriteAsync(async () =>
        {
            var slips = await db.PaySlips.Where(p => p.Month == month && !p.IsSimulated && p.Status == Unpaid).ToListAsync();
            if (slips.Count == 0) throw new BadRequestException("Everyone is already paid for this month.");
            foreach (var slip in slips)
            {
                slip.Status = Paid;
                slip.PaidAt = Now;
                slip.PaidByUserId = scope.UserId;
                slip.UpdatedAt = Now;
                Notify(slip.UserId, "PayrollPaid", "Salary paid", $"Your salary for {month:MM/yyyy} is paid: {slip.NetAmount:#,0} VND.",
                    slip.ParkingLotId ?? 0, month, "PaySlip", slip.Id.ToString());
            }
            await SaveAndPublishAsync(slips.First().ParkingLotId ?? 0);
            return new PayAllResultModel(month, slips.Count, slips.Sum(s => s.NetAmount));
        });
        return result;
    }

    public async Task<PaySlipModel> SlipAsync(long id)
    {
        var slip = await db.PaySlips.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id) ?? throw new NotFoundException("Payslip not found.");
        if (scope.Role != "Admin" && slip.UserId != scope.UserId) throw new NotFoundException("Payslip not found.");
        return (await SlipModelsAsync([slip]))[0];
    }

    public async Task<PayrollReportModel> AddAdjustmentAsync(PayAdjustmentInput input)
    {
        EnsureAdmin();
        var month = MonthOf(input.Month);
        var reason = input.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 500) throw new BadRequestException("Give a reason of at most 500 characters.");
        if (input.Amount == 0 || Math.Abs(input.Amount) > 100_000_000) throw new BadRequestException("Enter an amount other than 0, up to 100,000,000 VND.");
        if (!await db.Employees.AnyAsync(e => e.Id == input.EmployeeId)) throw new NotFoundException("Employee not found.");
        if (await db.PaySlips.AnyAsync(p => p.EmployeeId == input.EmployeeId && p.Month == month && !p.IsSimulated && p.Status == Paid))
            throw new BadRequestException("This month is already paid for this person. Add the adjustment to a later month.");
        db.PayAdjustments.Add(new PayAdjustment
        {
            EmployeeId = input.EmployeeId, Month = month, Amount = Round(input.Amount), Reason = reason, CreatedByUserId = scope.UserId, CreatedAt = Now,
        });
        await db.SaveChangesAsync();
        return await PayrollReportAsync(month, null, null, null, true);
    }

    /* Staff and Managers ------------------------------------------------ */

    private async Task<Payee> MePayeeAsync()
    {
        if (scope.Role is not (StaffRole or "Manager")) throw new ForbiddenException("Only Staff and Managers have payslips.");
        var userId = scope.UserId;
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.UserId == userId) ?? throw new NotFoundException("No employee record for this account.");
        var month = MonthOf(PayToday);
        return (await PayeesAsync(month, employee.Id)).FirstOrDefault()
            ?? new Payee(employee, scope.Role == "Manager" ? EmploymentTypes.Manager
                : EmploymentTypes.IsValid(employee.EmploymentType) ? employee.EmploymentType : EmploymentTypes.FullTime, employee.ParkingLotId);
    }

    public async Task<IReadOnlyList<PaySlipModel>> MyPayslipsAsync()
    {
        var me = await MePayeeAsync();
        var slips = await db.PaySlips.AsNoTracking().Where(p => p.EmployeeId == me.Employee.Id && p.Status == Paid)
            .OrderByDescending(p => p.PaidAt).Take(60).ToListAsync();
        return await SlipModelsAsync(slips);
    }

    public bool SimulationAllowed => pay.AllowSimulation;

    public async Task<PaySlipModel> SimulatePaydayAsync(SimulatePayInput input)
    {
        if (!pay.AllowSimulation) throw new ForbiddenException("Payday simulation is turned off.");
        var me = await MePayeeAsync();
        if (input.OvertimeHours is < 0 or > 200)
            throw new BadRequestException("Overtime hours must be a whole number between 0 and 200.");
        var lines = new List<PayLineModel>();
        var includeBase = input.IncludeBase && me.Kind != EmploymentTypes.Backup;
        if (includeBase)
        {
            var monthly = me.Kind == EmploymentTypes.Manager ? pay.ManagerMonthly : pay.FullTimeStaffMonthly;
            lines.Add(new PayLineModel { Kind = "Base", Label = "Base salary (simulated)", Quantity = 1, UnitAmount = monthly, Amount = monthly });
        }
        if (input.OvertimeHours > 0)
            lines.Add(new PayLineModel
            {
                Kind = me.Kind == EmploymentTypes.Backup ? "Hours" : "ExtraHours", Label = $"{(me.Kind == EmploymentTypes.Backup ? "Hours worked" : "Overtime")} ({input.OvertimeHours}h, simulated)",
                Quantity = input.OvertimeHours, UnitAmount = pay.HourlyRate, Amount = Round(input.OvertimeHours * pay.HourlyRate),
            });
        if (lines.Count == 0) throw new BadRequestException("Choose the base salary or enter some overtime hours.");

        var baseAmount = lines.Where(l => l.Kind == "Base").Sum(l => l.Amount);
        var extra = lines.Where(l => l.Kind != "Base").Sum(l => l.Amount);
        var slip = new PaySlip
        {
            EmployeeId = me.Employee.Id, UserId = me.Employee.UserId, ParkingLotId = me.LotId, Month = MonthOf(PayToday), Kind = me.Kind,
            BaseAmount = baseAmount, Overtime = extra, NetAmount = baseAmount + extra, Status = Paid, IsSimulated = true,
            PaidAt = Now, PaidByUserId = me.Employee.UserId, LinesJson = JsonSerializer.Serialize(lines, LineJson), CreatedAt = Now, UpdatedAt = Now,
        };
        db.PaySlips.Add(slip);
        await db.SaveChangesAsync();
        return (await SlipModelsAsync([slip]))[0];
    }

    /// <summary>Daily: on the 1st, prepares last month's payslips and tells the Admins.</summary>
    public async Task<int> PreparePayrollAsync()
    {
        var today = PayToday;
        if (today.Day != 1) return 0;
        var month = MonthOf(today).AddMonths(-1);
        await EnsureMonthSlipsAsync(month);
        var refId = month.ToString("yyyy-MM");
        var admins = await identity.Users.AsNoTracking().Where(u => u.Role == "Admin" && u.IsActive).Select(u => u.Id).ToListAsync();
        var told = await db.UserNotifications.AsNoTracking().Where(n => n.Type == "PayrollReady" && n.RefId == refId).Select(n => n.UserId).ToListAsync();
        var count = await db.PaySlips.CountAsync(p => p.Month == month && !p.IsSimulated && p.Status == Unpaid);
        if (count == 0) return 0;
        var total = await db.PaySlips.Where(p => p.Month == month && !p.IsSimulated && p.Status == Unpaid).SumAsync(p => p.NetAmount);
        foreach (var admin in admins.Except(told))
            Notify(admin, "PayrollReady", "Payroll ready", $"Payroll for {month:MM/yyyy} is ready: {count} people, {total:#,0} VND.", 0, month, "Payroll", refId);
        await SaveAndPublishAsync(0);
        PublishCommitted();
        return count;
    }
}
