using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;
public partial class WorkSchedulingService
{
    private record ActualWork(int AssignmentId, string UserId, DateTime PlannedStart, DateTime PlannedEnd, DateTime Start, DateTime End);
    private async Task<List<ActualWork>> ActualWorkAsync(IReadOnlyList<string> users, DateTime intervalFrom, DateTime intervalTo) => await (
        from at in db.DutyAttendances.AsNoTracking() join a in db.DutyAssignments.AsNoTracking() on at.DutyAssignmentId equals a.Id
        join s in db.DutyRosterShifts.AsNoTracking() on a.DutyRosterShiftId equals s.Id
        join r in db.DutyRosters.AsNoTracking() on s.DutyRosterId equals r.Id join e in db.Employees.AsNoTracking() on a.EmployeeId equals e.Id
        where users.Contains(e.UserId) && r.Status == "Published" && !a.IsCancelled && !s.IsCancelled
            && at.CheckInAt != null && at.CheckOutAt != null && at.CheckInAt < intervalTo && intervalFrom < at.CheckOutAt
        select new ActualWork(a.Id, e.UserId, s.StartsAt, s.EndsAt, at.CheckInAt.Value, at.CheckOutAt.Value)).ToListAsync();

    // Outside-shift hours are already overtime. Allocate only within-shift actual hours to the
    // agreed 48-hour weekly ordinary-hours limit, chronologically; no double counting.
    private (List<(DateTime Start, DateTime End)> Regular, string Key) OrdinaryWork(AttendanceContext c, DutyAttendance record, List<ActualWork> all)
    {
        var segments = new List<(DateTime, DateTime)>();
        if (record?.CheckInAt == null || record.CheckOutAt == null) return (segments, "");
        var start = record.CheckInAt > c.Shift.StartsAt ? record.CheckInAt.Value : c.Shift.StartsAt;
        var end = record.CheckOutAt < c.Shift.EndsAt ? record.CheckOutAt.Value : c.Shift.EndsAt;
        var zone = RotationZone(c.Shift.TimeZoneId);
        var first = WeekStart(DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Utc(start), zone)));
        var last = WeekStart(DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Utc(end), zone)));
        var relevant = all.Where(x => x.UserId == c.Employee.UserId && x.Start < end && x.End > DayBounds(first, zone).Start
            && (x.PlannedStart < c.Shift.StartsAt || (x.PlannedStart == c.Shift.StartsAt && x.AssignmentId <= c.Assignment.Id))).OrderBy(x => x.PlannedStart).ThenBy(x => x.AssignmentId).ToList();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { RotationRules.MaximumWeeklyHours, Relevant = relevant }))));
        if (end <= start) return (segments, key);
        for (var week = first; week <= last; week = week.AddDays(7)) {
            var bounds = (Start: DayBounds(week, zone).Start, End: DayBounds(week.AddDays(6), zone).End);
            var ownStart = start > bounds.Start ? start : bounds.Start;
            var ownEnd = end < bounds.End ? end : bounds.End;
            if (ownEnd <= ownStart) continue;
            var prior = relevant.Where(x => x.AssignmentId != c.Assignment.Id).Sum(x => Hours(
                x.Start > x.PlannedStart ? x.Start : x.PlannedStart, x.End < x.PlannedEnd ? x.End : x.PlannedEnd, bounds.Start, bounds.End));
            var remaining = RotationRules.MaximumWeeklyHours.HasValue ? Math.Max(0, RotationRules.MaximumWeeklyHours.Value - prior) : 168;
            var ordinaryEnd = ownStart.AddHours((double)Math.Min(remaining, (decimal)(ownEnd - ownStart).TotalHours));
            if (ordinaryEnd > ownStart) segments.Add((ownStart, ordinaryEnd));
        }
        return (segments, key);
    }
}
