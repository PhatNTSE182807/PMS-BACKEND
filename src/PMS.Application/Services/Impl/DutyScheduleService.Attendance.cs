using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.DutySchedule;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>Shift check-in / check-out, the POS shift check and the minutely absence / auto-end run.</summary>
public partial class DutyScheduleService
{
    private const string Working = "Working";
    private const string Completed = "Completed";
    private const string Absent = "Absent";

    private TimeSpan Early => TimeSpan.FromMinutes(options.EarlyStartMinutes);
    private TimeSpan AbsentAfter => TimeSpan.FromMinutes(options.AbsentAfterMinutes);
    private TimeSpan Grace => TimeSpan.FromMinutes(options.EndGraceMinutes);

    /// <summary>Last moment a shift can be started: its start + the absence delay, or later for someone put on it after it began.</summary>
    private DateTime StartDeadline(LotInfo lot, DateOnly date, int slot, Assignment a)
    {
        var start = SlotStart(lot, date, slot);
        var from = a.AssignedAt is DateTime at && at > start ? at : start;
        var end = SlotEnd(lot, date, slot);
        return from + AbsentAfter < end ? from + AbsentAfter : end;
    }

    private DutyShiftModel Shift(LotInfo lot, DateOnly date, int slot, DutyShiftAttendance record) => new()
    {
        ParkingLotId = lot.Id, LotName = lot.Name, Date = date, Slot = slot,
        StartsAt = SlotStart(lot, date, slot), EndsAt = SlotEnd(lot, date, slot),
        AttendanceStatus = record?.Status, IsAutoEnded = record?.IsAutoEnded == true,
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
        var open = records.Where(r => r.Status == Working && r.CheckInAt != null && r.CheckOutAt == null)
            .OrderByDescending(r => r.CheckInAt).FirstOrDefault();

        DutyShiftModel current = null;
        bool canStart = false, onShift = false;
        if (open != null)
        {
            current = Shift(lot, open.WorkDate, open.Slot, open);
            onShift = Now < current.EndsAt + Grace;
        }
        else
        {
            foreach (var ((date, slot), a) in schedule.Cells.Where(c => c.Value.EmployeeId == me.EmployeeId).OrderBy(c => c.Key.Item1).ThenBy(c => c.Key.Item2))
            {
                var start = SlotStart(lot, date, slot);
                if (Now < start - Early || Now >= StartDeadline(lot, date, slot, a)) continue;
                var record = records.FirstOrDefault(r => r.WorkDate == date && r.Slot == slot);
                if (record != null) continue;
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
            RequireShiftForPos = options.RequireShiftForPos, OnShift = onShift, CanStart = canStart, CanEnd = open != null,
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
            if (status.CanEnd) throw new BadRequestException("You are already on a shift. End it first.");
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

    public async Task<DutyStatusModel> EndShiftAsync()
    {
        var me = await CurrentStaffAsync();
        var lot = await LotAsync(me.ParkingLotId!.Value);
        await WriteAsync(async () =>
        {
            var open = await db.DutyShiftAttendances.Where(a => a.EmployeeId == me.EmployeeId && a.Status == Working && a.CheckOutAt == null)
                .OrderByDescending(a => a.CheckInAt).FirstOrDefaultAsync()
                ?? throw new BadRequestException("You are not on a shift.");
            open.Status = Completed;
            open.CheckOutAt = Now;
            open.UpdatedAt = Now;
            await SaveAndPublishAsync(open.ParkingLotId);
            return true;
        });
        return await StatusAsync(me, lot);
    }

    /// <summary>The POS check: the Staff user is checked in to a shift that has not ended (plus the grace period).</summary>
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
            if (Now < SlotEnd(lot, a.WorkDate, a.Slot) + Grace) return true;
        }
        return false;
    }

    /// <summary>The Manager records what really happened on a shift (forgot to check in, wrong time…). Both times empty: absent.</summary>
    public async Task<DutyWeekModel> AdjustAttendanceAsync(int lotId, DutyAttendanceInput input)
    {
        await EnsureManagerAsync(lotId);
        CheckSlot(input.Slot);
        var reason = input.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 500) throw new BadRequestException("Give a reason of at most 500 characters.");
        var lot = await LotAsync(lotId);
        var people = await PeopleAsync(lotId, [input.EmployeeId]);
        if (!people.TryGetValue(input.EmployeeId, out var person)) throw new BadRequestException("Choose a Staff member of this lot.");
        var start = SlotStart(lot, input.Date, input.Slot);
        var end = SlotEnd(lot, input.Date, input.Slot);
        if (start > Now) throw new BadRequestException("This shift has not started yet.");
        var checkIn = input.CheckInAt?.ToUniversalTime();
        var checkOut = input.CheckOutAt?.ToUniversalTime();
        if (checkOut != null && checkIn == null) throw new BadRequestException("Enter the check-in time too.");
        if (checkIn > Now || checkOut > Now) throw new BadRequestException("Times cannot be in the future.");
        if (checkOut < checkIn) throw new BadRequestException("Check-out cannot be before check-in.");
        if (checkIn != null && (checkIn < start - Early || checkIn >= end)) throw new BadRequestException("Check-in must be within the shift.");
        if (checkIn != null && checkOut == null && Now >= end + Grace) throw new BadRequestException("This shift is over: enter the check-out time too.");
        await EnsureMonthOpenAsync(input.EmployeeId, input.Date);

        await WriteAsync(async () =>
        {
            var record = await db.DutyShiftAttendances.SingleOrDefaultAsync(a => a.ParkingLotId == lotId && a.WorkDate == input.Date
                && a.Slot == input.Slot && a.EmployeeId == input.EmployeeId);
            if (record == null)
            {
                record = new DutyShiftAttendance { ParkingLotId = lotId, EmployeeId = input.EmployeeId, WorkDate = input.Date, Slot = input.Slot, CreatedAt = Now };
                db.DutyShiftAttendances.Add(record);
            }
            record.CheckInAt = checkIn;
            record.CheckOutAt = checkOut;
            record.Status = checkIn == null ? Absent : checkOut == null ? Working : Completed;
            record.IsAutoEnded = false;
            record.AdjustedByUserId = scope.UserId;
            record.AdjustReason = reason;
            record.UpdatedAt = Now;
            Notify(person.UserId, "DutyAttendanceChanged", "Attendance corrected",
                $"The Manager corrected your {input.Date:dd/MM/yyyy} {DutySlots.Label(input.Slot)} shift: {reason}", lotId, input.Date);
            await SaveAndPublishAsync(lotId);
            return true;
        });
        return await WeekAsync(lot, input.Date, null);
    }

    /// <summary>Attendance of a month that is already paid cannot change.</summary>
    private async Task EnsureMonthOpenAsync(int employeeId, DateOnly date)
    {
        var month = new DateOnly(date.Year, date.Month, 1);
        if (await db.PaySlips.AnyAsync(p => p.EmployeeId == employeeId && p.Month == month && !p.IsSimulated && p.Status == "Paid"))
            throw new BadRequestException("This month is already paid. Ask an Admin to add an adjustment to a later month.");
    }

    /// <summary>Minutely: marks missed shifts absent and ends shifts nobody ended. Returns the number of changes.</summary>
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
                    var start = SlotStart(lot, date, slot);
                    // Put on the shift after it began (or the board was created later): not marked absent automatically
                    if (a.AssignedAt is not DateTime assignedAt || assignedAt > start) continue;
                    if (Now <= StartDeadline(lot, date, slot, a) || records.Any(r => r.WorkDate == date && r.Slot == slot && r.EmployeeId == employeeId)) continue;
                    db.DutyShiftAttendances.Add(new DutyShiftAttendance
                    {
                        ParkingLotId = lotId, EmployeeId = employeeId, WorkDate = date, Slot = slot, Status = Absent,
                        CreatedAt = Now, UpdatedAt = Now,
                    });
                    var when = $"{date:dd/MM/yyyy} {DutySlots.Label(slot)}";
                    Notify(person.UserId, "DutyAbsent", "Marked absent", $"You did not start your {when} shift.", lotId, date);
                    Notify(lot.ManagerUserId, "DutyAbsent", "Staff absent", $"{person.FullName} did not start the {when} shift.", lotId, date);
                    count++;
                }

                foreach (var open in records.Where(r => r.Status == Working && r.CheckOutAt == null))
                {
                    var end = SlotEnd(lot, open.WorkDate, open.Slot);
                    if (Now <= end + Grace) continue;
                    open.Status = Completed;
                    open.CheckOutAt = open.CheckInAt > end ? open.CheckInAt : end;
                    open.IsAutoEnded = true;
                    open.UpdatedAt = Now;
                    count++;
                }
                if (count > 0) await SaveAndPublishAsync(lotId);
                return count;
            });
        }
        return changes;
    }
}
