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
    private static string PayrollHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static PayrollInterval ClipPayroll(DateTime start, DateTime end, DateTime from, DateTime to, string kind) =>
        new(Utc(start > from ? start : from), Utc(end < to ? end : to), kind);

    // The payroll snapshot is taken once, when the month's attendance is locked.
    private async Task<PayrollAttendanceSource> CapturePayrollSourceAsync(AttendancePeriodLot lot, AttendancePeriodReview review)
    {
        var existing = await db.PayrollAttendanceSources.SingleOrDefaultAsync(x => x.AttendancePeriodLotId == lot.Id);
        if (existing != null) return existing;
        if (lot.Status != "Locked") throw new ConflictException("Only locked attendance can be sealed as payroll evidence.");
        var people = new Dictionary<string, PayrollPerson>(); var assignments = new List<PayrollAssignmentEvidence>();
        var rosterModels = new Dictionary<int, DutyRosterModel>();
        foreach (var row in review.Rows) {
            var c = await LoadAttendanceContextAsync(row.AssignmentId, true);
            var record = await db.DutyAttendances.AsNoTracking().SingleOrDefaultAsync(x => x.DutyAssignmentId == row.AssignmentId);
            if ((record?.Revision ?? 0) != row.Attendance.Revision || record?.CheckInAt != row.Attendance.CheckInAt || record?.CheckOutAt != row.Attendance.CheckOutAt)
                throw new ConflictException($"Assignment #{row.AssignmentId}: stored attendance does not match the locked snapshot.");
            var ordinary = OrdinaryWork(c, record, await ActualWorkAsync([c.Employee.UserId], c.Shift.StartsAt.AddDays(-8), c.Shift.EndsAt.AddDays(8)));
            var intervals = ordinary.Regular.Select(x => ClipPayroll(x.Start, x.End, lot.StartsAt, lot.EndsAt, "Regular")).Where(x => x.End > x.Start).ToList();
            if (row.Pay.OvertimeStatus == "Approved" && record?.CheckInAt != null && record.CheckOutAt != null) {
                var cursor = record.CheckInAt.Value;
                foreach (var segment in ordinary.Regular.OrderBy(x => x.Start)) {
                    if (segment.Start > cursor) intervals.Add(ClipPayroll(cursor, segment.Start, lot.StartsAt, lot.EndsAt, "Overtime"));
                    if (segment.End > cursor) cursor = segment.End;
                }
                if (cursor < record.CheckOutAt.Value) intervals.Add(ClipPayroll(cursor, record.CheckOutAt.Value, lot.StartsAt, lot.EndsAt, "Overtime"));
            }
            intervals = intervals.Where(x => x.End > x.Start).OrderBy(x => x.Start).ToList();
            var approvedHours = intervals.Sum(x => (decimal)(x.End - x.Start).Ticks / TimeSpan.TicksPerHour);
            if (Math.Abs(approvedHours - row.Pay.ApprovedHours) > .00001m)
                throw new ConflictException($"Assignment #{row.AssignmentId}: approved hour segments cannot be verified against locked totals. Record a subsequent-period adjustment instead.");
            var leave = await db.WorkLeaveRequests.AsNoTracking().SingleOrDefaultAsync(x => x.DutyAssignmentId == row.AssignmentId && x.Status == "Approved");
            if (!rosterModels.TryGetValue(c.Roster.Id, out var rosterModel)) rosterModels[c.Roster.Id] = rosterModel = await ModelAsync(c.Roster);
            var shift = rosterModel.Shifts.Single(s => s.Id == c.Shift.Id);
            // A slip's source contains only its owner's assignment; aggregate staffing counts remain available.
            shift = shift with { Assignments = shift.Assignments.Where(a => a.Id == row.AssignmentId).Select(a => a with {
                    StaffName = row.StaffName, Attendance = row.Attendance with { CanStart = false, CanEnd = false, CanReopen = false } }).ToList(), AttendanceLocked = true,
                Trail = shift.Trail.Where(a => a.AssignmentId == row.AssignmentId).ToList(), OpenShifts = [], ManagerCoverage = [] };
            var history = await db.DutyAttendanceEvents.AsNoTracking().Where(x => x.DutyAssignmentId == row.AssignmentId && x.Revision <= row.Attendance.Revision).OrderBy(x => x.Revision).ToListAsync();
            foreach (var h in history) h.CreatedAt = Utc(h.CreatedAt);
            assignments.Add(new(row.AssignmentId, c.Employee.Id, c.Employee.UserId, row.StaffName, row.ShiftName,
                row.WorkDate, c.Shift.TimeZoneId, Utc(row.StartsAt), Utc(row.EndsAt), row.Attendance.Revision,
                record?.CheckInAt is DateTime actualIn ? Utc(actualIn) : null, record?.CheckOutAt is DateTime actualOut ? Utc(actualOut) : null,
                row.Attendance.IsLate, row.Attendance.AutoEnded, row.Attendance.Status, intervals, leave?.Id, leave?.IsPaid == true,
                JsonSerializer.Serialize(history), JsonSerializer.Serialize(row.Pay.OvertimeHistory)) {
                    RosterId = c.Roster.Id, RosterName = c.Roster.Name, RosterRevision = c.Roster.Revision, ShiftSnapshot = shift,
                    Attendance = row.Attendance with { CanStart = false, CanEnd = false, CanReopen = false }
                });
            people[c.Employee.UserId] = new(c.Employee.UserId, c.Employee.Id, row.StaffName, "Staff");
        }
        var managerIds = review.ManagerDays.Select(x => x.ManagerUserId).ToHashSet();
        foreach (var carry in review.Adjustments) {
            var employee = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == carry.EmployeeId);
            people.TryAdd(employee.UserId, new(employee.UserId, employee.Id, employee.FullName, "Staff"));
        }
        var managerId = await db.ParkingLots.Where(x => x.Id == lot.ParkingLotId).Select(x => x.ManagerUserId).SingleAsync();
        if (!string.IsNullOrEmpty(managerId)) managerIds.Add(managerId);
        foreach (var id in managerIds) {
            var e = await db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == id);
            people[id] = new(id, e?.Id, review.ManagerDays.FirstOrDefault(x => x.ManagerUserId == id)?.ManagerName ?? e?.FullName ?? "Manager", "Manager");
        }
        var holidayDefinitions = await db.Holidays.AsNoTracking().ToListAsync(); var holidays = new List<PayrollHoliday>();
        for (var date = review.Period.FromDate; date <= review.Period.ToDate; date = date.AddDays(1))
            foreach (var h in holidayDefinitions.Where(h => h.HolidayDate == date || (h.IsRecurringYearly && h.HolidayDate.Month == date.Month && h.HolidayDate.Day == date.Day)))
                holidays.Add(new(date, h.Name));
        var evidence = new PayrollEvidence(1, lot.Id, lot.ParkingLotId, review.Lot.LotName, lot.TimeZoneId,
            review.Period.FromDate, review.Period.ToDate, Utc(lot.StartsAt), Utc(lot.EndsAt), people.Values.OrderBy(x => x.UserId).ToList(), assignments,
            review.ManagerDays, holidays, review.Adjustments);
        var json = JsonSerializer.Serialize(evidence);
        var source = new PayrollAttendanceSource { AttendancePeriodLotId = lot.Id, SnapshotJson = json, SnapshotHash = PayrollHash(json),
            CreatedBy = scope.UserId, CreatedAt = Now };
        db.PayrollAttendanceSources.Add(source);
        PayrollAuditEntry("AttendanceSourceSealed", lot.ParkingLotId, null, "Sealed atomically with attendance lock.", null, new { lot.Id, source.SnapshotHash });
        return source;
    }

    /// <summary>Hours carried between locked attendance months at a lot, for Admin to value as payroll adjustments.</summary>
    public async Task<IReadOnlyList<AttendanceCarryAdjustment>> PayrollCarriesAsync(int lotId)
    {
        EnsurePeriodAdmin(); await scope.EnsureCanManageAsync(lotId);
        var lotIds = await db.AttendancePeriodLots.Where(l => l.ParkingLotId == lotId && l.Status == "Locked").Select(l => l.Id).ToListAsync();
        var sources = await db.PayrollAttendanceSources.AsNoTracking().Where(s => lotIds.Contains(s.AttendancePeriodLotId)).ToListAsync();
        return sources.SelectMany(s => JsonSerializer.Deserialize<PayrollEvidence>(s.SnapshotJson)?.CarryAdjustments ?? []).ToList();
    }
}
