using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;
public partial class WorkSchedulingService
{
    private record WorkRange(int LotId, DateTime Start, DateTime End);

    // Every schedule/attendance/request persistence point goes through this guard, including jobs.
    // It runs inside WriteAsync's shared SQL application lock, before data is written.
    private async Task<int> SaveWorkChangesAsync()
    {
        db.ChangeTracker.DetectChanges();
        await EnsurePayrollImmutableChangesAsync();
        var changes = db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        var ranges = new List<WorkRange>();
        var assignmentIds = new HashSet<int>(); var shiftIds = new HashSet<int>(); var rosterIds = new HashSet<int>();
        async Task Day(int lot, DateOnly date)
        {
            var zoneId = await db.ParkingLots.Where(l => l.Id == lot).Select(l => l.TimeZoneId).SingleAsync();
            var zone = ParkingText.FindTimeZone(zoneId);
            ranges.Add(new(lot, TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), zone),
                TimeZoneInfo.ConvertTimeToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), zone)));
        }
        foreach (var entry in changes)
        {
            var versions = entry.State == EntityState.Modified || entry.State == EntityState.Deleted
                ? new[] { entry.OriginalValues.ToObject(), entry.Entity } : new[] { entry.Entity };
            foreach (var value in versions)
                switch (value)
                {
                    case DutyAttendance x:
                        assignmentIds.Add(x.DutyAssignmentId);
                        // Also check corrected times outside the planned interval, before and after an edit.
                        if (x.CheckInAt.HasValue && x.CheckOutAt.HasValue) {
                            var a = await db.DutyAssignments.FindAsync(x.DutyAssignmentId);
                            if (a != null) {
                                var s = await db.DutyRosterShifts.FindAsync(a.DutyRosterShiftId);
                                var r = s == null ? null : await db.DutyRosters.FindAsync(s.DutyRosterId);
                                if (r != null) ranges.Add(new(r.ParkingLotId, x.CheckInAt.Value, x.CheckOutAt.Value));
                            }
                        }
                        break;
                    case DutyAssignment x: shiftIds.Add(x.DutyRosterShiftId); assignmentIds.Add(x.Id); break;
                    case DutyRosterShift x:
                        shiftIds.Add(x.Id);
                        var roster = await db.DutyRosters.FindAsync(x.DutyRosterId);
                        if (roster != null) ranges.Add(new(roster.ParkingLotId, x.StartsAt, x.EndsAt));
                        break;
                    case DutyRoster x when entry.State == EntityState.Modified && entry.Property("Status").IsModified:
                        rosterIds.Add(x.Id); break;
                    case WorkLeaveRequest x: assignmentIds.Add(x.DutyAssignmentId); break;
                    case WorkSwapRequest x: assignmentIds.Add(x.AssignmentId); assignmentIds.Add(x.OtherAssignmentId); break;
                    case WorkOpenShift x: shiftIds.Add(x.DutyRosterShiftId); break;
                    case WorkOpenShiftClaim x:
                        var slot = await db.WorkOpenShifts.FindAsync(x.WorkOpenShiftId);
                        if (slot != null) shiftIds.Add(slot.DutyRosterShiftId);
                        break;
                    case WorkManagerCoverage x: shiftIds.Add(x.DutyRosterShiftId); break;
                    case AttendanceOvertimeDecision x: assignmentIds.Add(x.DutyAssignmentId); break;
                    case ManagerWorkDay x: await Day(x.ParkingLotId, x.WorkDate); break;
                    case AttendanceCarryAdjustment x:
                        var target = await db.AttendancePeriodLots.FindAsync(x.TargetPeriodLotId);
                        if (target != null) ranges.Add(new(target.ParkingLotId, target.StartsAt, target.EndsAt));
                        break;
                }
        }
        if (assignmentIds.Count > 0)
        {
            var assignments = await db.DutyAssignments.AsNoTracking().Where(a => assignmentIds.Contains(a.Id)).ToListAsync();
            foreach (var a in assignments) shiftIds.Add(a.DutyRosterShiftId);
            var records = await (from a in db.DutyAssignments.AsNoTracking()
                join s in db.DutyRosterShifts.AsNoTracking() on a.DutyRosterShiftId equals s.Id
                join r in db.DutyRosters.AsNoTracking() on s.DutyRosterId equals r.Id
                join at in db.DutyAttendances.AsNoTracking() on a.Id equals at.DutyAssignmentId
                where assignmentIds.Contains(a.Id) && at.CheckInAt != null && at.CheckOutAt != null
                select new { r.ParkingLotId, at.CheckInAt, at.CheckOutAt }).ToListAsync();
            ranges.AddRange(records.Select(x => new WorkRange(x.ParkingLotId, x.CheckInAt.Value, x.CheckOutAt.Value)));
        }
        if (shiftIds.Count > 0 || rosterIds.Count > 0)
        {
            var shifts = await (from s in db.DutyRosterShifts.AsNoTracking() join r in db.DutyRosters.AsNoTracking() on s.DutyRosterId equals r.Id
                where shiftIds.Contains(s.Id) || rosterIds.Contains(r.Id) select new { r.ParkingLotId, s.StartsAt, s.EndsAt }).ToListAsync();
            ranges.AddRange(shifts.Select(x => new WorkRange(x.ParkingLotId, x.StartsAt, x.EndsAt)));
        }
        if (ranges.Count > 0)
        {
            ranges = ranges.Distinct().ToList();
            // A changed earlier shift can change which later hours exceed the weekly limit.
            // Invalidate dependent later periods too; a locked dependent period cannot be rewritten.
            var affectsWeeklyHours = changes.Any(e => e.Entity switch {
                DutyAttendance at => e.State == EntityState.Added ? at.CheckInAt.HasValue || at.CheckOutAt.HasValue
                    : e.Property("CheckInAt").IsModified || e.Property("CheckOutAt").IsModified,
                DutyAssignment => e.State != EntityState.Modified || e.Property("EmployeeId").IsModified || e.Property("DutyRosterShiftId").IsModified || e.Property("IsCancelled").IsModified,
                DutyRosterShift => e.State != EntityState.Modified || e.Property("StartsAt").IsModified || e.Property("EndsAt").IsModified || e.Property("IsCancelled").IsModified,
                _ => false
            });
            if (affectsWeeklyHours) {
                foreach (var range in ranges.Where(r => r.End > r.Start).ToList()) {
                    var zoneId = await db.ParkingLots.Where(l => l.Id == range.LotId).Select(l => l.TimeZoneId).SingleAsync();
                    var zone = ParkingText.FindTimeZone(zoneId);
                    var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Utc(range.End.AddTicks(-1)), zone));
                    var weekEnd = DayBounds(WeekStart(last).AddDays(6), zone).End;
                    if (weekEnd > range.End) ranges.Add(new(range.LotId, range.Start, weekEnd));
                }
                var employeeIds = changes.Where(e => e.Entity is DutyAssignment).Select(e => ((DutyAssignment)e.Entity).EmployeeId).ToList();
                employeeIds.AddRange(await db.DutyAssignments.AsNoTracking().Where(a => assignmentIds.Contains(a.Id) || shiftIds.Contains(a.DutyRosterShiftId))
                    .Select(a => a.EmployeeId).ToListAsync());
                var users = await db.Employees.AsNoTracking().Where(e => employeeIds.Contains(e.Id)).Select(e => e.UserId).Distinct().ToListAsync();
                if (users.Count > 0) {
                    var earliest = ranges.Min(r => r.Start); var latest = ranges.Max(r => r.End);
                    var dependent = await (from a in db.DutyAssignments.AsNoTracking() join s in db.DutyRosterShifts.AsNoTracking() on a.DutyRosterShiftId equals s.Id
                        join r in db.DutyRosters.AsNoTracking() on s.DutyRosterId equals r.Id join e in db.Employees.AsNoTracking() on a.EmployeeId equals e.Id
                        where users.Contains(e.UserId) && !a.IsCancelled && !s.IsCancelled && r.Status == "Published"
                            && s.StartsAt < latest && earliest < s.EndsAt select new { r.ParkingLotId, s.StartsAt, s.EndsAt }).ToListAsync();
                    var sourceRanges = ranges.ToList();
                    foreach (var x in dependent.Where(x => sourceRanges.Any(r => r.LotId != x.ParkingLotId && r.Start < x.EndsAt && x.StartsAt < r.End)))
                        ranges.Add(new(x.ParkingLotId, x.StartsAt, x.EndsAt));
                }
            }
            var lots = ranges.Select(x => x.LotId).Distinct().ToList();
            var periods = await db.AttendancePeriodLots.Where(p => lots.Contains(p.ParkingLotId)).ToListAsync();
            var affected = periods.Where(p => ranges.Any(x => x.LotId == p.ParkingLotId && x.Start < p.EndsAt && p.StartsAt < x.End)).ToList();
            if (affected.Any(p => p.Status == "Locked"))
                throw new ConflictException("Attendance period is locked. Record an adjustment in a subsequent open period instead.");
            foreach (var p in affected)
            {
                if (p.Status == "Confirmed") {
                    PeriodEvent(p, "ConfirmationInvalidated", "Schedule, attendance or approval data changed; Manager must confirm again.", p.SnapshotJson, null);
                    p.Status = "Open"; p.ConfirmedAt = null; p.ConfirmedBy = null; p.SnapshotJson = null;
                }
                p.Revision++;
            }
        }
        return await db.SaveChangesAsync();
    }

    private void PeriodEvent(AttendancePeriodLot p, string action, string reason, object before, object after)
    {
        db.AttendancePeriodEvents.Add(new AttendancePeriodEvent { AttendancePeriodLotId = p.Id, Action = action,
            ActorId = string.IsNullOrEmpty(scope.UserId) ? "System" : scope.UserId, Reason = reason, CreatedAt = Now,
            BeforeJson = before == null ? null : JsonSerializer.Serialize(before), AfterJson = after == null ? null : JsonSerializer.Serialize(after) });
    }

    private async Task<bool> IsAttendanceLockedAsync(int lotId, DateTime start, DateTime end) =>
        await db.AttendancePeriodLots.AnyAsync(p => p.ParkingLotId == lotId && p.Status == "Locked" && start < p.EndsAt && p.StartsAt < end);
}
