using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.WorkScheduling;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;
public partial class WorkSchedulingService
{
    private void EnsurePeriodAdmin()
    {
        if (scope.Role != "Admin") throw new ForbiddenException("Only Admin manages attendance periods and Manager workdays.");
    }
    public async Task<object> AttendancePeriodDirectoryAsync()
    {
        EnsurePeriodAdmin();
        return await db.ParkingLots.AsNoTracking().Where(l => !l.IsDeleted).OrderBy(l => l.Name)
            .Select(l => new { l.Id, l.Name, l.ManagerUserId }).ToListAsync();
    }
    private async Task<PeriodModel> PeriodModelAsync(AttendancePeriod p)
    {
        var lots = await (from x in db.AttendancePeriodLots join l in db.ParkingLots on x.ParkingLotId equals l.Id
            where x.AttendancePeriodId == p.Id orderby l.Name
            select new PeriodLotModel(x.Id, x.ParkingLotId, l.Name, x.TimeZoneId, x.Status, x.Revision, x.ConfirmedAt)).ToListAsync();
        return new(p.Id, p.Name, p.FromDate, p.ToDate, p.Status == "Locked" ? "Locked" : lots.Count > 0 && lots.All(l => l.Status == "Confirmed") ? "Confirmed" : "Open",
            p.Revision, p.LockedAt, lots);
    }
    public async Task<IReadOnlyList<PeriodModel>> AttendancePeriodsAsync(int? lotId)
    {
        if (scope.Role != "Admin") { if (!lotId.HasValue) throw new ForbiddenException("Select a managed lot."); await EnsureManagerAsync(lotId.Value); }
        else if (lotId.HasValue) await scope.EnsureCanManageAsync(lotId.Value);
        var ids = await db.AttendancePeriodLots.Where(x => !lotId.HasValue || x.ParkingLotId == lotId).Select(x => x.AttendancePeriodId).Distinct().ToListAsync();
        var periods = await db.AttendancePeriods.Where(x => ids.Contains(x.Id)).OrderByDescending(x => x.FromDate).ToListAsync();
        var result = new List<PeriodModel>();
        foreach (var p in periods) {
            var model = await PeriodModelAsync(p);
            if (scope.Role != "Admin") model = model with { Lots = model.Lots.Where(x => x.ParkingLotId == lotId).ToList() };
            result.Add(model);
        }
        return result;
    }
    public async Task<PeriodModel> CreateAttendancePeriodAsync(CreateAttendancePeriodInput input)
    {
        EnsurePeriodAdmin();
        if (input.Month == default || input.Month.Day != 1 || input.Month.Year < 1900 || input.Month.Year >= 9999 || input.LotIds == null || input.LotIds.Length == 0)
            throw new BadRequestException("Select a month and at least one lot.");
        // One attendance period is exactly one calendar month, so it matches one payroll month.
        var fromDate = input.Month; var toDate = input.Month.AddMonths(1).AddDays(-1);
        var name = string.IsNullOrWhiteSpace(input.Name) ? input.Month.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture) : input.Name;
        return await WriteAsync(async () => {
            var ids = input.LotIds.Distinct().ToList();
            var lots = await db.ParkingLots.Where(x => ids.Contains(x.Id) && !x.IsDeleted).ToListAsync();
            if (lots.Count != ids.Count) throw new BadRequestException("Invalid parking lot.");
            if (await (from x in db.AttendancePeriodLots join p in db.AttendancePeriods on x.AttendancePeriodId equals p.Id
                where ids.Contains(x.ParkingLotId) && fromDate <= p.ToDate && p.FromDate <= toDate select x.Id).AnyAsync())
                throw new ConflictException("This month already has an attendance period for one of these lots.");
            var period = new AttendancePeriod { Name = Name(name), FromDate = fromDate, ToDate = toDate, CreatedBy = scope.UserId, CreatedAt = Now };
            db.AttendancePeriods.Add(period); await SaveWorkChangesAsync();
            foreach (var lot in lots) {
                var zone = ParkingText.FindTimeZone(lot.TimeZoneId);
                var row = new AttendancePeriodLot { AttendancePeriodId = period.Id, ParkingLotId = lot.Id, TimeZoneId = zone.Id,
                    StartsAt = TimeZoneInfo.ConvertTimeToUtc(fromDate.ToDateTime(TimeOnly.MinValue), zone),
                    EndsAt = TimeZoneInfo.ConvertTimeToUtc(toDate.AddDays(1).ToDateTime(TimeOnly.MinValue), zone) };
                db.AttendancePeriodLots.Add(row); await SaveWorkChangesAsync(); PeriodEvent(row, "Created", null, null, input);
            }
            await SaveWorkChangesAsync(); return await PeriodModelAsync(period);
        });
    }
    public async Task<AttendancePeriodReview> ConfirmAttendancePeriodAsync(int periodId, int lotId, PeriodActionInput input)
    {
        await EnsureManagerAsync(lotId);
        await WriteAsync(async () => {
            var p = await db.AttendancePeriodLots.SingleOrDefaultAsync(x => x.AttendancePeriodId == periodId && x.ParkingLotId == lotId)
                ?? throw new NotFoundException("Period lot not found.");
            CheckRevision(p.Revision, input.Revision);
            if (p.Status == "Locked") throw new ConflictException("This attendance period is locked.");
            var before = await BuildPeriodReviewAsync(p);
            if (before.Warnings.Count > 0) throw new BadRequestException(string.Join(" ", before.Warnings));
            var reason = RequestReason(input.Reason);
            p.Status = "Confirmed"; p.ConfirmedAt = Now; p.ConfirmedBy = scope.UserId; p.Revision++;
            await SaveWorkChangesAsync();
            p.SnapshotJson = JsonSerializer.Serialize(await BuildPeriodReviewAsync(p));
            PeriodEvent(p, "Confirmed", reason, before, p.SnapshotJson); await SaveWorkChangesAsync(); return 0;
        });
        QueueAttendancePush(lotId, []); return await ReviewPeriodAsync(periodId, lotId);
    }
    public async Task<PeriodModel> LockAttendancePeriodAsync(int periodId, PeriodActionInput input)
    {
        EnsurePeriodAdmin();
        var model = await WriteAsync(async () => {
            var period = await db.AttendancePeriods.SingleOrDefaultAsync(x => x.Id == periodId) ?? throw new NotFoundException("Period not found.");
            CheckRevision(period.Revision, input.Revision);
            if (period.Status == "Locked") throw new ConflictException("Period is already locked.");
            var lots = await db.AttendancePeriodLots.Where(x => x.AttendancePeriodId == periodId).ToListAsync();
            var unconfirmed = lots.Where(x => x.Status != "Confirmed").Select(x => x.ParkingLotId).ToList();
            if (unconfirmed.Count > 0 && !input.AcknowledgeUnconfirmed)
                throw new ConflictException($"Unconfirmed lots: {string.Join(", ", unconfirmed)}. Explicit acknowledgment and reason are required.");
            foreach (var p in lots) {
                var review = await BuildPeriodReviewAsync(p);
                if (review.Warnings.Count > 0) throw new BadRequestException($"Lot {p.ParkingLotId}: {string.Join(" ", review.Warnings)}");
            }
            var reason = RequestReason(input.Reason);
            period.Status = "Locked"; period.LockedAt = Now; period.LockedBy = scope.UserId; period.Revision++;
            foreach (var p in lots) {
                var prior = p.Status; p.Status = "Locked"; p.Revision++;
                PeriodEvent(p, "Locked", reason, new { Status = prior, UnconfirmedAcknowledged = input.AcknowledgeUnconfirmed }, new { Status = p.Status });
            }
            await SaveWorkChangesAsync();
            foreach (var p in lots) {
                var review = await BuildPeriodReviewAsync(p);
                p.SnapshotJson = JsonSerializer.Serialize(review);
                await CapturePayrollSourceAsync(p, review);
            }
            await SaveWorkChangesAsync(); return await PeriodModelAsync(period);
        });
        foreach (var lot in model.Lots) QueueAttendancePush(lot.ParkingLotId, []);
        return model;
    }

    public async Task<ManagerWorkDay> SaveManagerWorkDayAsync(int lotId, ManagerWorkDayInput input)
    {
        EnsurePeriodAdmin(); await scope.EnsureCanManageAsync(lotId);
        return await WriteAsync(async () => {
            var zone = ParkingText.FindTimeZone(await db.ParkingLots.Where(l => l.Id == lotId).Select(l => l.TimeZoneId).SingleAsync());
            if (input.Kind is not ("Work" or "Leave" or "PaidLeave" or "UnpaidLeave" or "Absent") || input.WorkDate == default || input.WorkDate > DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Utc(Now), zone)))
                throw new BadRequestException("Select a completed workday or leave day.");
            if (!await identity.Users.AnyAsync(x => x.Id == input.ManagerUserId && x.Role == "Manager" && x.IsActive)
                || !await db.ParkingLots.AnyAsync(x => x.Id == lotId && x.ManagerUserId == input.ManagerUserId))
                throw new BadRequestException("Select this lot's active Manager.");
            var row = await db.ManagerWorkDays.SingleOrDefaultAsync(x => x.ManagerUserId == input.ManagerUserId && x.WorkDate == input.WorkDate);
            var before = row == null ? null : JsonSerializer.Serialize(row);
            if (row != null) { CheckRevision(row.Revision, input.Revision); row.Revision++; }
            else { if (input.Revision != 0) throw new ConflictException("Workday changed."); row = new ManagerWorkDay { ManagerUserId = input.ManagerUserId, WorkDate = input.WorkDate }; db.ManagerWorkDays.Add(row); }
            row.ParkingLotId = lotId; row.Kind = input.Kind; row.Reason = RequestReason(input.Reason);
            await SaveWorkChangesAsync();
            var periodLots = await (from p in db.AttendancePeriods join l in db.AttendancePeriodLots on p.Id equals l.AttendancePeriodId
                where l.ParkingLotId == lotId && p.FromDate <= input.WorkDate && input.WorkDate <= p.ToDate select l).ToListAsync();
            foreach (var p in periodLots) PeriodEvent(p, "ManagerDayRecorded", row.Reason, before, row);
            var notices = new List<long>();
            await WorkflowEventAsync(lotId, "ManagerWorkDay", row.Id, "Recorded", before, row, row.Reason, [row.ManagerUserId], notices);
            QueueAttendancePush(lotId, notices); return row;
        });
    }
    public async Task<AttendanceCarryAdjustment> AddCarryAdjustmentAsync(int targetPeriodLotId, CarryAdjustmentInput input)
    {
        var result = await WriteAsync(async () => {
            var target = await db.AttendancePeriodLots.SingleOrDefaultAsync(x => x.Id == targetPeriodLotId) ?? throw new NotFoundException("Target period lot not found.");
            await EnsureManagerAsync(target.ParkingLotId);
            CheckRevision(target.Revision, input.Revision);
            var source = await db.AttendancePeriodLots.SingleOrDefaultAsync(x => x.Id == input.SourcePeriodLotId) ?? throw new NotFoundException("Source period lot not found.");
            if (source.Status != "Locked" || target.Status == "Locked" || source.ParkingLotId != target.ParkingLotId || target.StartsAt < source.EndsAt)
                throw new BadRequestException("Choose a locked source and a subsequent open period at the same lot.");
            var snapshot = JsonSerializer.Deserialize<AttendancePeriodReview>(source.SnapshotJson);
            if (snapshot == null || !snapshot.Employees.Any(x => x.EmployeeId == input.EmployeeId)
                || (input.AssignmentId.HasValue && !snapshot.Rows.Any(x => x.AssignmentId == input.AssignmentId && x.EmployeeId == input.EmployeeId)))
                throw new BadRequestException("Employee and source assignment must belong to the locked source snapshot.");
            if (input.Hours == 0 || Math.Abs(input.Hours) > 744 || decimal.Round(input.Hours, 4) != input.Hours)
                throw new BadRequestException("Enter a non-zero hours adjustment with at most four decimals, within 744 hours.");
            var row = new AttendanceCarryAdjustment { SourcePeriodLotId = source.Id, TargetPeriodLotId = target.Id,
                EmployeeId = input.EmployeeId, DutyAssignmentId = input.AssignmentId, Hours = input.Hours,
                Reason = RequestReason(input.Reason), CreatedBy = scope.UserId, CreatedAt = Now };
            db.AttendanceCarryAdjustments.Add(row); await SaveWorkChangesAsync();
            PeriodEvent(target, "CarryAdjustmentApproved", row.Reason, null, row); await SaveWorkChangesAsync(); return row;
        });
        var lotId = await db.AttendancePeriodLots.Where(x => x.Id == targetPeriodLotId).Select(x => x.ParkingLotId).SingleAsync();
        QueueAttendancePush(lotId, []); return result;
    }
}
