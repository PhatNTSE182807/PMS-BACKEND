using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.WorkScheduling;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;
public partial class WorkSchedulingService
{
    private async Task EnsureNoOpenSlotBypassAsync(DutyRoster roster, int shiftId)
    {
        if (roster.Status == "Published" && await db.WorkOpenShifts.AnyAsync(o => o.DutyRosterShiftId == shiftId && o.Status == "Open" && (o.ExpiresAt == null || o.ExpiresAt > Now)))
            throw new BadRequestException("This shift has an open vacancy. Fill it through the vacancy instead of assigning directly.");
    }
    private async Task<int> StaffShortageAsync(DutyRosterShift shift)
    {
        var absentIds = await db.DutyAttendances.Where(a => a.IsAbsent).Select(a => a.DutyAssignmentId).ToListAsync();
        var count = await db.DutyAssignments.CountAsync(a => a.DutyRosterShiftId == shift.Id && !a.IsCancelled && !absentIds.Contains(a.Id));
        count += await db.WorkManagerCoverages.CountAsync(c => c.DutyRosterShiftId == shift.Id);
        return Math.Max(0, shift.MinimumStaff - count);
    }

    private async Task<List<RotationIssue>> ReplacementIssuesAsync(int lotId, int employeeId, DutyRosterShift shift, IEnumerable<int> exclude = null, bool overtime = false)
    {
        var person = (await AvailableSchedulingStaffAsync(lotId)).FirstOrDefault(e => e.EmployeeId == employeeId);
        if (person == null) return [new("Unavailable", shift.WorkDate, employeeId, "Staff must be active at this lot.")];
        var all = await RotationIntervalsAsync(lotId, shift.WorkDate, shift.WorkDate);
        var excluded = exclude?.ToHashSet() ?? [];
        var leave = await LeaveSource.ReadAsync(lotId, shift.StartsAt, shift.EndsAt);
        return CheckRotationInterval(employeeId, person.UserId, shift, all.Where(a => !excluded.Contains(a.AssignmentId)), leave)
            .Where(i => i.Code != "WeeklyHours" || !overtime).ToList();
    }
    private async Task EnsureReplacementAsync(int lotId, int employeeId, DutyRosterShift shift, IEnumerable<int> exclude = null, bool overtime = false)
    {
        var issues = await ReplacementIssuesAsync(lotId, employeeId, shift, exclude, overtime);
        if (issues.Count > 0) throw new BadRequestException(string.Join(" ", issues.Select(i => i.Message).Distinct()));
    }
    private async Task<List<RosterStaffModel>> EligibleReplacementsAsync(int lotId, DutyRosterShift shift)
    {
        var staff = await AvailableSchedulingStaffAsync(lotId);
        var all = await RotationIntervalsAsync(lotId, shift.WorkDate, shift.WorkDate);
        var leave = await LeaveSource.ReadAsync(lotId, shift.StartsAt, shift.EndsAt);
        return staff.Where(e => CheckRotationInterval(e.EmployeeId, e.UserId, shift, all, leave).Count == 0).ToList();
    }

    // A vacancy opened before its shift starts expires at the start; one opened later (a no-show or
    // emergency leave during the shift) can still be filled until the shift ends.
    private DateTime VacancyExpiry(DutyRosterShift shift) => Now < shift.StartsAt ? shift.StartsAt : shift.EndsAt;
    private bool IsVacancyExpired(WorkOpenShift slot, DutyRosterShift shift) => Now >= (slot.ExpiresAt ?? shift.EndsAt);

    private async Task<WorkOpenShift> NewOpenShiftAsync(DutyRosterShift shift, string reason, int? originalId,
        int? leaveId, List<long> notices)
    {
        var roster = await db.DutyRosters.SingleAsync(r => r.Id == shift.DutyRosterId);
        var needed = await StaffShortageAsync(shift);
        var reserved = await db.WorkOpenShifts.CountAsync(s => s.DutyRosterShiftId == shift.Id && s.Status == "Open" && (s.ExpiresAt == null || s.ExpiresAt > Now));
        if (needed <= reserved) return null;
        var slot = new WorkOpenShift { ParkingLotId = roster.ParkingLotId, DutyRosterShiftId = shift.Id,
            OriginalAssignmentId = originalId, LeaveRequestId = leaveId, Reason = reason, CreatedAt = Now, ExpiresAt = VacancyExpiry(shift) };
        db.WorkOpenShifts.Add(slot); await SaveWorkChangesAsync();
        // The Manager and every Staff member who could take the shift are told about it.
        var recipients = new List<string> { await RequestManagerAsync(slot.ParkingLotId) };
        recipients.AddRange((await EligibleReplacementsAsync(slot.ParkingLotId, shift)).Select(e => e.UserId));
        await WorkflowEventAsync(slot.ParkingLotId, "OpenShift", slot.Id, "Opened", null, slot, slot.Reason, recipients, notices);
        return slot;
    }

    public async Task<WorkOpenShift> CreateOpenShiftAsync(int lotId, CreateOpenShiftInput input)
    {
        await EnsureManagerAsync(lotId);
        return await WorkflowAsync(lotId, async notices => {
            var shift = await db.DutyRosterShifts.SingleOrDefaultAsync(s => s.Id == input.ShiftId && !s.IsCancelled) ?? throw new NotFoundException("Shift not found.");
            var roster = await FindRosterAsync(lotId, shift.DutyRosterId);
            if (roster.Status != "Published" || Now >= shift.EndsAt) throw new BadRequestException("Choose a Published shift that has not ended.");
            var snapshots = await RequestSnapshotsAsync([roster.Id]);
            var slot = await NewOpenShiftAsync(shift, RequestReason(input.Reason), null, null, notices)
                ?? throw new BadRequestException("This shift already has enough staff or open vacancies.");
            await TouchRequestRostersAsync(snapshots, "OpenShiftCreated", input.Reason); return slot;
        });
    }

    /// <summary>Staff claim or withdraw; the Manager confirms a claim, assigns Staff directly, covers the shift, rejects a claim or closes the vacancy.</summary>
    public async Task<WorkOpenShift> ActOnOpenShiftAsync(int id, WorkRequestCommand input)
    {
        if (input.Reason?.Length > 1000) throw new BadRequestException("Reason must be at most 1000 characters.");
        var initial = await db.WorkOpenShifts.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id) ?? throw new NotFoundException("Vacancy not found.");
        return await WorkflowAsync(initial.ParkingLotId, async notices => {
            var slot = await db.WorkOpenShifts.SingleAsync(s => s.Id == id); CheckRevision(slot.Revision, input.Revision);
            var shift = await db.DutyRosterShifts.SingleAsync(s => s.Id == slot.DutyRosterShiftId);
            var roster = await FindRosterAsync(slot.ParkingLotId, shift.DutyRosterId);
            if (slot.Status != "Open" || roster.Status != "Published" || shift.IsCancelled || IsVacancyExpired(slot, shift))
                throw new ConflictException("This vacancy is no longer open.");
            var before = JsonSerializer.Serialize(slot);
            if (input.Action is "Claim" or "Withdraw")
            {
                var person = await RequestActorAsync(slot.ParkingLotId);
                var claim = await db.WorkOpenShiftClaims.SingleOrDefaultAsync(c => c.WorkOpenShiftId == id && c.EmployeeId == person.Id);
                if (input.Action == "Claim") {
                    await EnsureReplacementAsync(slot.ParkingLotId, person.Id, shift);
                    if (claim != null) throw new ConflictException("You already responded to this vacancy.");
                    db.WorkOpenShiftClaims.Add(new WorkOpenShiftClaim { WorkOpenShiftId = id, EmployeeId = person.Id, CreatedAt = Now });
                }
                else {
                    if (claim?.Status != "Claimed") throw new BadRequestException("No pending claim to withdraw.");
                    claim.Status = "Withdrawn";
                }
                slot.Revision++;
                await WorkflowEventAsync(slot.ParkingLotId, "OpenShift", id, input.Action, before, slot, input.Reason,
                    [person.UserId, await RequestManagerAsync(slot.ParkingLotId)], notices); return slot;
            }
            await EnsureManagerAsync(slot.ParkingLotId); var reason = RequestReason(input.Reason);
            if (input.Action == "RejectClaim") {
                var claim = await db.WorkOpenShiftClaims.SingleOrDefaultAsync(c => c.Id == input.ClaimId && c.WorkOpenShiftId == id && c.Status == "Claimed")
                    ?? throw new BadRequestException("Pending claim not found.");
                claim.Status = "Rejected"; slot.Revision++;
                await WorkflowEventAsync(slot.ParkingLotId, "OpenShift", id, "ClaimRejected", before, slot, reason,
                    [await db.Employees.Where(e => e.Id == claim.EmployeeId).Select(e => e.UserId).SingleAsync()], notices); return slot;
            }
            if (input.Action == "Close") {
                var closeRecipients = await (from c in db.WorkOpenShiftClaims join e in db.Employees on c.EmployeeId equals e.Id
                    where c.WorkOpenShiftId == id && c.Status == "Claimed" select e.UserId).ToListAsync();
                closeRecipients.Add(await RequestManagerAsync(slot.ParkingLotId));
                slot.Status = "Closed"; slot.Revision++; await RejectRemainingClaimsAsync(slot.Id, "Rejected");
                await WorkflowEventAsync(slot.ParkingLotId, "OpenShift", id, "Closed", before, slot, reason,
                    closeRecipients, notices); return slot;
            }
            if (input.Action is not ("Confirm" or "Assign" or "ManagerCover")) throw new BadRequestException("Unknown vacancy action.");
            if (await StaffShortageAsync(shift) == 0) throw new ConflictException("This shift already has enough staff. Close the vacancy.");
            int? employeeId = input.EmployeeId;
            if (input.Action == "Confirm") employeeId = (await db.WorkOpenShiftClaims.SingleOrDefaultAsync(c => c.Id == input.ClaimId && c.WorkOpenShiftId == id && c.Status == "Claimed"))?.EmployeeId
                ?? throw new BadRequestException("Choose a pending claim to confirm.");
            if (input.Action != "ManagerCover") {
                if (!employeeId.HasValue) throw new BadRequestException("Choose Staff.");
                await EnsureReplacementAsync(slot.ParkingLotId, employeeId.Value, shift, overtime: input.Action == "Assign" && input.AllowWeeklyOvertime);
            }
            var snapshots = await RequestSnapshotsAsync([roster.Id]);
            var users = new List<string> { await RequestManagerAsync(slot.ParkingLotId) };
            if (input.Action == "ManagerCover") {
                db.WorkManagerCoverages.Add(new WorkManagerCoverage { WorkOpenShiftId = id, DutyRosterShiftId = shift.Id,
                    ManagerUserId = scope.UserId, Reason = reason, CreatedAt = Now });
            }
            else {
                var assignment = new DutyAssignment { DutyRosterShiftId = shift.Id, EmployeeId = employeeId.Value,
                    Source = "Replacement", OriginalAssignmentId = slot.OriginalAssignmentId, ReplacementReason = reason,
                    AllowWeeklyOvertime = input.Action == "Assign" && input.AllowWeeklyOvertime, IsManuallyEdited = true, AttendanceTrackingFrom = Now };
                db.DutyAssignments.Add(assignment); await SaveWorkChangesAsync(); slot.ReplacementAssignmentId = assignment.Id;
                users.Add(await db.Employees.Where(e => e.Id == employeeId.Value).Select(e => e.UserId).SingleAsync());
            }
            slot.Status = "Filled"; slot.Revision++;
            foreach (var claim in await db.WorkOpenShiftClaims.Where(c => c.WorkOpenShiftId == id && c.Status == "Claimed").ToListAsync()) {
                claim.Status = input.Action == "Confirm" && claim.Id == input.ClaimId ? "Confirmed" : "Rejected";
                users.Add(await db.Employees.Where(e => e.Id == claim.EmployeeId).Select(e => e.UserId).SingleAsync());
            }
            await TouchRequestRostersAsync(snapshots, "ReplacementConfirmed", reason);
            await WorkflowEventAsync(slot.ParkingLotId, "OpenShift", id, input.Action == "ManagerCover" ? "ManagerCovered" : "ReplacementConfirmed", before, slot, reason, users, notices);
            return slot;
        });
    }

    /// <summary>Marks vacancies past their expiry as Expired and tells pending claimants. No other timers remain.</summary>
    private async Task<int> ExpireDueVacanciesAsync(Dictionary<int, List<long>> notices)
    {
        var slots = await db.WorkOpenShifts.Where(o => o.Status == "Open" && o.ExpiresAt != null && o.ExpiresAt <= Now).OrderBy(o => o.Id).Take(200).ToListAsync();
        var changed = 0;
        foreach (var slot in slots) {
            var shift = await db.DutyRosterShifts.FindAsync(slot.DutyRosterShiftId);
            if (await IsAttendanceLockedAsync(slot.ParkingLotId, shift.StartsAt, shift.EndsAt)) continue;
            if (!notices.TryGetValue(slot.ParkingLotId, out var list)) notices[slot.ParkingLotId] = list = [];
            var before = JsonSerializer.Serialize(slot);
            var users = await (from c in db.WorkOpenShiftClaims join e in db.Employees on c.EmployeeId equals e.Id
                where c.WorkOpenShiftId == slot.Id && c.Status == "Claimed" select e.UserId).ToListAsync();
            users.Add(await RequestManagerAsync(slot.ParkingLotId));
            slot.Status = "Expired"; slot.Revision++; await RejectRemainingClaimsAsync(slot.Id, "Expired");
            await WorkflowEventAsync(slot.ParkingLotId, "OpenShift", slot.Id, "Expired", before, slot, "The vacancy expired.", users, list);
            changed++;
        }
        return changed;
    }
    private async Task RejectRemainingClaimsAsync(int slotId, string status)
    {
        foreach (var claim in await db.WorkOpenShiftClaims.Where(c => c.WorkOpenShiftId == slotId && c.Status == "Claimed").ToListAsync()) claim.Status = status;
    }

    private async Task<List<long>> StopOffersForReopenAsync(int assignmentId, string reason)
    {
        var notices = new List<long>();
        foreach (var slot in await db.WorkOpenShifts.Where(s => s.OriginalAssignmentId == assignmentId && s.Status == "Open").ToListAsync()) {
            var before = JsonSerializer.Serialize(slot); slot.Status = "Closed"; slot.Revision++;
            var users = await (from c in db.WorkOpenShiftClaims join e in db.Employees on c.EmployeeId equals e.Id
                where c.WorkOpenShiftId == slot.Id && c.Status == "Claimed" select e.UserId).ToListAsync();
            await RejectRemainingClaimsAsync(slot.Id, "Rejected");
            await WorkflowEventAsync(slot.ParkingLotId, "OpenShift", slot.Id, "OriginalShiftReopened", before, slot, reason, users, notices);
        }
        return notices;
    }
}
