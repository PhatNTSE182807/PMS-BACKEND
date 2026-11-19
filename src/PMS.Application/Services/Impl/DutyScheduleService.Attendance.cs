using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.DutySchedule;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Shift check-in, the POS shift check and the minutely run. A Staff member may start their shift from
/// 30 minutes before it begins until it ends (after the start time it is marked late); nobody started it by
/// its end: absent. A started shift cannot be ended early: it ends by itself at the scheduled end.
/// </summary>
public partial class DutyScheduleService
{
    private const string Working = "Working";
    private const string Completed = "Completed";
    private const string Absent = "Absent";

    private TimeSpan Early => TimeSpan.FromMinutes(options.EarlyStartMinutes);

    /// <summary>Checked in after the shift's start time (or, when put on the shift after it began, after that moment).</summary>
    private bool IsLate(LotInfo lot, DutyShiftAttendance record, DateTime? assignedAt = null)
    {
        if (record?.CheckInAt is not DateTime checkIn) return false;
        var start = SlotStart(lot, record.WorkDate, record.Slot);
        var from = assignedAt is DateTime at && at > start ? at : start;
        return checkIn > from.AddMinutes(1);
    }

    private DutyShiftModel Shift(LotInfo lot, DateOnly date, int slot, DutyShiftAttendance record, DateTime? assignedAt = null) => new()
    {
        ParkingLotId = lot.Id, LotName = lot.Name, Date = date, Slot = slot,
        StartsAt = SlotStart(lot, date, slot), EndsAt = SlotEnd(lot, date, slot),
        AttendanceStatus = record?.Status, IsLate = IsLate(lot, record, assignedAt),
        CheckInAt = record?.CheckInAt, CheckOutAt = record?.CheckOutAt,
    };

    public async Task<DutyStatusModel> StatusAsync()
    {
        var me = await CurrentStaffAsync();
        var lot = await LotAsync(me.ParkingLotId!.Value);
        return await StatusAsync(me, lot);
    }

    private async Task<DutyStatusModel> StatusAsync(Person me, LotInfo lot)
    {
        var today = Today(lot);
        var schedule = await LoadScheduleAsync(lot.Id, today.AddDays(-1), today.AddDays(28));
        var records = await db.DutyShiftAttendances.AsNoTracking()
            .Where(a => a.EmployeeId == me.EmployeeId && a.WorkDate >= today.AddDays(-1) && a.WorkDate <= today.AddDays(1)).ToListAsync();
        // A started shift whose end has passed counts as finished even before the minutely run closes it
        var open = records.Where(r => r.Status == Working && r.CheckInAt != null && r.CheckOutAt == null && Now < SlotEnd(lot, r.WorkDate, r.Slot))
            .OrderByDescending(r => r.CheckInAt).FirstOrDefault();

        DutyShiftModel current = null;
        var canStart = false;
        if (open != null) current = Shift(lot, open.WorkDate, open.Slot, open, schedule.At(open.WorkDate, open.Slot).AssignedAt);
        else
        {
            foreach (var ((date, slot), a) in schedule.Cells.Where(c => c.Value.EmployeeId == me.EmployeeId).OrderBy(c => c.Key.Item1).ThenBy(c => c.Key.Item2))
            {
                if (Now < SlotStart(lot, date, slot) - Early || Now >= SlotEnd(lot, date, slot)) continue;
                if (records.Any(r => r.WorkDate == date && r.Slot == slot)) continue;
                current = Shift(lot, date, slot, null);
                canStart = true;
                break;
            }
        }

        var next = schedule.Cells.Where(c => c.Value.EmployeeId == me.EmployeeId)
            .Select(c => (Date: c.Key.Item1, Slot: c.Key.Item2))
            .Where(c => SlotStart(lot, c.Date, c.Slot) > Now && !(current != null && c.Date == current.Date && c.Slot == current.Slot))
            .OrderBy(c => c.Date).ThenBy(c => c.Slot)
            .Select(c => Shift(lot, c.Date, c.Slot, null)).FirstOrDefault();

        return new DutyStatusModel
        {
            RequireShiftForPos = options.RequireShiftForPos, OnShift = open != null, CanStart = canStart,
            Current = current, Next = next, EmploymentType = me.EmploymentType,
        };
    }

    public async Task<DutyStatusModel> StartShiftAsync()
    {
        var me = await CurrentStaffAsync();
        var lot = await LotAsync(me.ParkingLotId!.Value);
        await WriteAsync(async () =>
        {
            var status = await StatusAsync(me, lot);
            if (status.OnShift) throw new BadRequestException("You are already on a shift.");
            if (!status.CanStart || status.Current == null)
                throw new BadRequestException("No shift of yours is open for check-in now. A shift opens 30 minutes before it starts.");
            db.DutyShiftAttendances.Add(new DutyShiftAttendance
            {
                ParkingLotId = lot.Id, EmployeeId = me.EmployeeId, WorkDate = status.Current.Date, Slot = status.Current.Slot,
                Status = Working, CheckInAt = Now, CreatedAt = Now, UpdatedAt = Now,
            });
            await SaveAndPublishAsync(lot.Id);
            return true;
        });
        return await StatusAsync(me, lot);
    }

    /// <summary>The POS check: the Staff user is checked in to a shift that has not ended.</summary>
    public async Task<bool> IsOnShiftAsync(string userId)
    {
        var employeeId = await db.Employees.AsNoTracking().Where(e => e.UserId == userId).Select(e => (int?)e.Id).FirstOrDefaultAsync();
        if (employeeId == null) return false;
        var open = await db.DutyShiftAttendances.AsNoTracking()
            .Where(a => a.EmployeeId == employeeId && a.Status == Working && a.CheckInAt != null && a.CheckOutAt == null)
            .Select(a => new { a.ParkingLotId, a.WorkDate, a.Slot }).ToListAsync();
        foreach (var a in open)
        {
            var lot = await LotAsync(a.ParkingLotId);
            if (Now < SlotEnd(lot, a.WorkDate, a.Slot)) return true;
        }
        return false;
    }

    /// <summary>Minutely: ends started shifts at their scheduled end and marks shifts nobody started absent. Returns the number of changes.</summary>
    public async Task<int> UpdateDueAttendanceAsync()
    {
        var changes = 0;
        var lotIds = await db.DutyPatterns.AsNoTracking().Select(p => p.ParkingLotId)
            .Union(db.DutyOverrides.AsNoTracking().Select(o => o.ParkingLotId)).Distinct().ToListAsync();
        foreach (var lotId in lotIds)
        {
            var lot = await LotAsync(lotId);
            changes += await WriteAsync(async () =>
            {
                var count = 0;
                var today = Today(lot);
                var schedule = await LoadScheduleAsync(lotId, today.AddDays(-1), today);
                var records = await db.DutyShiftAttendances.Where(a => a.ParkingLotId == lotId && a.WorkDate >= today.AddDays(-1)).ToListAsync();
                var people = await PeopleAsync(lotId, schedule.Cells.Values.Where(a => a.EmployeeId != null).Select(a => a.EmployeeId!.Value));
                var leaves = await LeavesAsync(people.Keys, today.AddDays(-1), today);

                foreach (var ((date, slot), a) in schedule.Cells)
                {
                    if (a.EmployeeId is not int employeeId || !people.TryGetValue(employeeId, out var person) || !person.IsActive) continue;
                    // Waiting for the Manager to decide a leave request: not absent
                    if (leaves.Any(r => r.EmployeeId == employeeId && r.FromDate <= date && r.ToDate >= date)) continue;
                    var end = SlotEnd(lot, date, slot);
                    // The board was set up after this shift began (not a later change for this day): nothing to mark
                    if (a.Override == null && (a.AssignedAt is not DateTime planned || planned > SlotStart(lot, date, slot))) continue;
                    if (Now < end || records.Any(r => r.WorkDate == date && r.Slot == slot && r.EmployeeId == employeeId)) continue;
                    db.DutyShiftAttendances.Add(new DutyShiftAttendance
                    {
                        ParkingLotId = lotId, EmployeeId = employeeId, WorkDate = date, Slot = slot, Status = Absent,
                        CreatedAt = Now, UpdatedAt = Now,
                    });
                    var when = $"{date:dd/MM/yyyy} {DutySlots.Label(slot)}";
                    Notify(person.UserId, "DutyAbsent", "Marked absent", $"You did not come to your {when} shift.", lotId, date);
                    Notify(lot.ManagerUserId, "DutyAbsent", "Staff absent", $"{person.FullName} did not come to the {when} shift.", lotId, date);
                    count++;
                }

                foreach (var open in records.Where(r => r.Status == Working && r.CheckOutAt == null))
                {
                    var end = SlotEnd(lot, open.WorkDate, open.Slot);
                    if (Now < end) continue;
                    open.Status = Completed;
                    open.CheckOutAt = open.CheckInAt > end ? open.CheckInAt : end;
                    open.UpdatedAt = Now;
                    count++;
                }
                if (count > 0) await SaveAndPublishAsync(lotId);
                return count;
            });
        }
        return changes;
    }

    /// <summary>Ends the shift of someone taken off it while working (emergency replacement).</summary>
    private async Task CloseReplacedAttendanceAsync(int lotId, DateOnly date, int slot, int employeeId)
    {
        var record = await db.DutyShiftAttendances.SingleOrDefaultAsync(a => a.ParkingLotId == lotId && a.WorkDate == date && a.Slot == slot
            && a.EmployeeId == employeeId && a.Status == Working && a.CheckOutAt == null);
        if (record == null) return;
        record.Status = Completed;
        record.CheckOutAt = Now;
        record.UpdatedAt = Now;
    }
}
