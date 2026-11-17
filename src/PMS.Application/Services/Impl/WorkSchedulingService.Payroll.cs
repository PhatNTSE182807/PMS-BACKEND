using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.WorkScheduling;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;
public partial class WorkSchedulingService
{
    private void PayrollAuditEntry(string action, int? lotId, int? slipId, string reason, object before, object after) =>
        db.PayrollAudits.Add(new PayrollAudit { ActorId = scope.UserId, Action = action, ParkingLotId = lotId, PayrollSlipId = slipId,
            Reason = reason, BeforeJson = before == null ? null : JsonSerializer.Serialize(before), AfterJson = after == null ? null : JsonSerializer.Serialize(after), CreatedAt = Now });

    private static DateOnly PayrollMonth(DateOnly date)
    {
        if (date == default || date.Day != 1 || date.Year < 1900 || date.Year >= 9999) throw new BadRequestException("Select the first date of an explicit payroll month.");
        return date;
    }
    public async Task<object> PayrollDirectoryAsync()
    {
        EnsurePeriodAdmin();
        var users = await identity.Users.AsNoTracking().Where(x => x.Role == "Staff" || x.Role == "Manager").Select(x => new { x.Id, x.Role, x.IsActive }).ToListAsync();
        var ids = users.Select(x => x.Id).ToList();
        var employees = await db.Employees.AsNoTracking().Where(x => ids.Contains(x.UserId)).ToListAsync();
        var lots = await db.ParkingLots.AsNoTracking().Where(x => !x.IsDeleted).OrderBy(x => x.Name).Select(x => new { x.Id, x.Name, x.ManagerUserId }).ToListAsync();
        return new { People = users.Select(u => new { UserId = u.Id, u.Role, u.IsActive,
            Name = employees.FirstOrDefault(e => e.UserId == u.Id)?.FullName ?? u.Role,
            EmployeeId = employees.FirstOrDefault(e => e.UserId == u.Id)?.Id,
            ParkingLotId = employees.FirstOrDefault(e => e.UserId == u.Id)?.ParkingLotId }).ToList(), Lots = lots };
    }
    public async Task<IReadOnlyList<PayrollProfile>> PayrollProfilesAsync(int? lotId)
    {
        IQueryable<PayrollProfile> query = db.PayrollProfiles.AsNoTracking();
        if (scope.Role == "Manager") {
            if (!lotId.HasValue) throw new BadRequestException("Choose a managed lot."); await EnsureManagerAsync(lotId.Value);
            var staffIds = await identity.Users.Where(x => x.Role == "Staff").Select(x => x.Id).ToListAsync();
            var ids = await db.Employees.Where(e => e.ParkingLotId == lotId && staffIds.Contains(e.UserId)).Select(e => e.UserId).ToListAsync();
            query = query.Where(p => p.Role == "Staff" && ids.Contains(p.UserId));
        } else EnsurePeriodAdmin();
        var result = await query.OrderBy(x => x.UserId).ThenByDescending(x => x.EffectiveFrom).ToListAsync();
        var profileUsers = result.Select(p => p.UserId).ToList();
        var names = await db.Employees.AsNoTracking().Where(e => profileUsers.Contains(e.UserId)).Select(e => new { e.UserId, e.FullName }).ToListAsync();
        foreach (var p in result) p.EmployeeName = names.FirstOrDefault(e => e.UserId == p.UserId)?.FullName ?? p.Role;
        PayrollAuditEntry("ProfilesViewed", lotId, null, null, null, new { ProfileIds = result.Select(p => p.Id).ToArray() }); await SaveWorkChangesAsync();
        return result;
    }
    public async Task<PayrollProfile> SavePayrollProfileAsync(int? id, PayrollProfileInput input)
    {
        EnsurePeriodAdmin();
        return await WriteAsync(async () => {
            var user = await identity.Users.SingleOrDefaultAsync(x => x.Id == input.UserId && (x.Role == "Staff" || x.Role == "Manager"))
                ?? throw new BadRequestException("Salary profiles are only for Staff and Manager; Admin salary is not managed.");
            if (input.Role != user.Role || input.EffectiveFrom.Year < 1900 || input.EffectiveFrom.Year >= 9999)
                throw new BadRequestException("Select the employee's role and a valid effective date.");
            var reason = RequestReason(input.Reason);
            if (new[] { input.HourlyRate, input.MonthlyRate, input.StandardWorkDays,
                input.NightFactor, input.HolidayFactor, input.OvertimeFactor }.Any(x => x.HasValue && decimal.Round(x.Value, 6) != x.Value))
                throw new BadRequestException("Salary values support up to six decimal places; unsupported precision is not silently rounded.");
            if (new[] { input.HourlyRate, input.MonthlyRate }.Any(x => x < 0 || x > 1000000000m)
                || new[] { input.NightFactor, input.HolidayFactor, input.OvertimeFactor }.Any(x => x.HasValue && (x < 1 || x > 1000))
                || input.StandardWorkDays is <= 0 or > 31 || input.MoneyDecimals is < 0 or > 4
                || (input.NightStart.HasValue && input.NightEnd.HasValue && input.NightStart == input.NightEnd)
                || (input.Currency != null && (input.Currency.Length != 3 || !input.Currency.All(char.IsAsciiLetter)))
                || (input.RoundingMode != null && input.RoundingMode is not ("ToEven" or "AwayFromZero")))
                throw new BadRequestException("Invalid salary profile values. Missing values may remain unconfigured; calculation will report them.");
            var row = id.HasValue ? await db.PayrollProfiles.SingleOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("Salary profile not found.") : null;
            if (row != null && (row.UserId != input.UserId || row.Role != input.Role || row.EffectiveFrom != input.EffectiveFrom))
                throw new BadRequestException("Create a new effective version to change the employee, role or effective date.");
            if (await db.PayrollSlips.AnyAsync(x => x.UserId == input.UserId && x.Status != "Draft" && x.PayMonth >= new DateOnly(input.EffectiveFrom.Year, input.EffectiveFrom.Month, 1)))
                throw new ConflictException("This effective salary change would affect approved/paid payroll. Use a subsequent effective date or a subsequent-period adjustment.");
            if (await db.PayrollProfiles.AnyAsync(x => x.UserId == input.UserId && x.EffectiveFrom == input.EffectiveFrom && x.Id != id))
                throw new ConflictException("A profile already exists on this effective date; edit that uncommitted version with its revision.");
            var before = row == null ? null : JsonSerializer.Serialize(row);
            if (row != null) { CheckRevision(row.Revision, input.Revision); }
            var revision = row?.Revision + 1 ?? 1; var created = row?.CreatedAt ?? Now;
            row ??= new PayrollProfile();
            row.UserId = input.UserId; row.Role = user.Role; row.EffectiveFrom = input.EffectiveFrom;
            row.HourlyRate = input.Role == "Staff" ? input.HourlyRate : null; row.MonthlyRate = input.Role == "Manager" ? input.MonthlyRate : null;
            row.NightStart = input.NightStart; row.NightEnd = input.NightEnd; row.NightFactor = input.NightFactor;
            row.HolidayFactor = input.HolidayFactor; row.OvertimeFactor = input.OvertimeFactor;
            row.OvertimeCombination = "PremiumThenOvertime"; row.PaidLeaveMethod = "PlannedHours"; row.PaidLeaveHoursPerDay = null;
            row.StandardWorkDays = input.StandardWorkDays;
            row.Currency = input.Currency?.ToUpperInvariant(); row.MoneyDecimals = input.MoneyDecimals; row.RoundingMode = input.RoundingMode;
            row.Reason = reason; row.Revision = revision; row.CreatedAt = created; row.CreatedBy = scope.UserId;
            if (!id.HasValue) db.PayrollProfiles.Add(row);
            await SaveWorkChangesAsync(); PayrollAuditEntry("ProfileSaved", null, null, reason, before, row); await SaveWorkChangesAsync();
            return row;
        });
    }

    private async Task<List<PayrollSourceBundle>> LoadPayrollSourcesAsync(DateOnly month, int lotId)
    {
        // The month's attendance period covers exactly that calendar month; its snapshot was taken when it was locked.
        PayrollMonth(month); var last = month.AddMonths(1).AddDays(-1);
        var period = await (from l in db.AttendancePeriodLots.AsNoTracking() join p in db.AttendancePeriods.AsNoTracking() on l.AttendancePeriodId equals p.Id
            where l.ParkingLotId == lotId && p.FromDate == month && p.ToDate == last select new { l.Id, l.Status }).SingleOrDefaultAsync();
        if (period?.Status != "Locked") throw new BadRequestException("Lock this month's attendance before calculating payroll.");
        var source = await db.PayrollAttendanceSources.AsNoTracking().SingleOrDefaultAsync(s => s.AttendancePeriodLotId == period.Id)
            ?? throw new BadRequestException("This month's locked attendance has no payroll snapshot.");
        if (PayrollHash(source.SnapshotJson) != source.SnapshotHash) throw new ConflictException("Sealed payroll source failed its integrity check.");
        var evidence = JsonSerializer.Deserialize<PayrollEvidence>(source.SnapshotJson) ?? throw new ConflictException("Invalid sealed payroll source.");
        return [new(source.Id, source.SnapshotHash, evidence)];
    }
    private async Task<List<PayrollCalculation>> CalculatePayrollAsync(PayrollGenerateInput input, string onlyUser = null)
    {
        var month = PayrollMonth(input.PayMonth); await scope.EnsureCanManageAsync(input.ParkingLotId);
        if (input.Role != null && input.Role is not ("Staff" or "Manager")) throw new BadRequestException("Choose Staff, Manager or all payroll roles.");
        var sources = await LoadPayrollSourcesAsync(month, input.ParkingLotId);
        var people = sources.SelectMany(s => s.Evidence.People).Where(x => input.Role == null || x.Role == input.Role).GroupBy(x => new { x.UserId, x.Role }).Select(g => g.First()).ToList();
        var adjustments = await db.PayrollAdjustments.AsNoTracking().Where(a => a.ParkingLotId == input.ParkingLotId && a.PayMonth == month).ToListAsync();
        foreach (var a in adjustments.Where(a => (input.Role == null || a.Role == input.Role) && !people.Any(p => p.UserId == a.UserId && p.Role == a.Role))) {
            var sourceSlip = a.SourceSlipId.HasValue ? await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.Id == a.SourceSlipId) : null;
            if (sourceSlip == null) throw new BadRequestException("An adjustment employee missing from locked attendance requires an earlier source payslip.");
            people.Add(new(a.UserId, sourceSlip.EmployeeId, sourceSlip.EmployeeName, sourceSlip.Role));
        }
        onlyUser ??= string.IsNullOrWhiteSpace(input.UserId) ? null : input.UserId;
        if (onlyUser != null) {
            people = people.Where(p => p.UserId == onlyUser).ToList();
            if (people.Count == 0) throw new BadRequestException("The selected employee has no matching locked source or traced adjustment at this lot/month.");
        }
        var userIds = people.Select(x => x.UserId).ToList();
        var profiles = await db.PayrollProfiles.AsNoTracking().Where(x => userIds.Contains(x.UserId)).OrderBy(x => x.EffectiveFrom).ToListAsync();
        var carries = sources.SelectMany(s => s.Evidence.CarryAdjustments).ToList();
        var carryIds = carries.Select(c => c.Id).ToList();
        var carryValuations = await db.PayrollAdjustments.AsNoTracking().Where(a => a.SourceCarryId.HasValue && carryIds.Contains(a.SourceCarryId.Value)).ToListAsync();
        foreach (var carry in carries) {
            var user = sources.SelectMany(s => s.Evidence.People).FirstOrDefault(p => p.EmployeeId == carry.EmployeeId)?.UserId
                ?? throw new ConflictException("Locked carry employee identity is missing from the sealed source.");
            if (!people.Any(p => p.UserId == user)) continue;
            if (!carryValuations.Any(a => a.SourceCarryId == carry.Id)) throw new BadRequestException($"Attendance carry adjustment #{carry.Id}: Admin must explicitly supply its unit rate and payroll month before calculating payroll.");
        }
        var result = new List<PayrollCalculation>();
        foreach (var person in people.OrderBy(x => x.UserId)) {
            if (person.Role == "Manager" && await db.PayrollSlips.AnyAsync(s => s.UserId == person.UserId && s.Role == "Manager" && s.PayMonth == month && s.ParkingLotId != input.ParkingLotId))
                throw new ConflictException("This Manager already has a monthly payslip at another lot; a monthly salary cannot be paid twice.");
            result.Add(PayrollCalculator.Calculate(month, person, sources, profiles, adjustments));
        }
        return result;
    }
    public async Task<object> PreviewPayrollAsync(PayrollGenerateInput input)
    {
        EnsurePeriodAdmin(); var result = await CalculatePayrollAsync(input);
        PayrollAuditEntry("PayrollPreviewed", input.ParkingLotId, null, null, null, new { input.PayMonth, input.Role, Users = result.Select(x => x.UserId).ToArray() }); await SaveWorkChangesAsync();
        return result;
    }
    public async Task<IReadOnlyList<PayrollSlip>> GeneratePayrollAsync(PayrollGenerateInput input)
    {
        EnsurePeriodAdmin(); var reason = RequestReason(input.Reason);
        return await WriteAsync(async () => {
            var plans = await CalculatePayrollAsync(input); var output = new List<PayrollSlip>();
            foreach (var plan in plans) {
                var slip = await db.PayrollSlips.SingleOrDefaultAsync(s => s.UserId == plan.UserId && s.ParkingLotId == input.ParkingLotId && s.PayMonth == input.PayMonth);
                if (slip?.Status is "Approved" or "Paid") throw new ConflictException($"{plan.EmployeeName}: approved/paid payslips cannot be regenerated.");
            }
            foreach (var plan in plans) {
                var slip = await db.PayrollSlips.SingleOrDefaultAsync(s => s.UserId == plan.UserId && s.ParkingLotId == input.ParkingLotId && s.PayMonth == input.PayMonth);
                var before = slip == null ? null : JsonSerializer.Serialize(new { Slip = slip, Lines = await db.PayrollLines.Where(l => l.PayrollSlipId == slip.Id).ToListAsync() });
                if (slip == null) {
                    slip = new PayrollSlip { UserId = plan.UserId, EmployeeId = plan.EmployeeId, EmployeeName = plan.EmployeeName, Role = plan.Role,
                        ParkingLotId = input.ParkingLotId, PayMonth = input.PayMonth, CreatedAt = Now };
                    db.PayrollSlips.Add(slip);
                } else {
                    // Exact retries are idempotent; old draft explanations stay in audit before replacement.
                    if (slip.BasisHash == plan.BasisHash) { output.Add(slip); continue; }
                    db.PayrollLines.RemoveRange(await db.PayrollLines.Where(l => l.PayrollSlipId == slip.Id).ToListAsync()); slip.Revision++;
                }
                slip.Total = plan.Total; slip.Currency = plan.Currency; slip.BasisHash = plan.BasisHash; slip.BasisJson = plan.BasisJson;
                slip.SourceIdsJson = JsonSerializer.Serialize(plan.SourceIds); await SaveWorkChangesAsync();
                foreach (var line in plan.Lines) { line.PayrollSlipId = slip.Id; db.PayrollLines.Add(line); }
                PayrollAuditEntry("DraftCalculated", input.ParkingLotId, slip.Id, reason, before, plan); await SaveWorkChangesAsync(); output.Add(slip);
            }
            return output;
        });
    }
}
