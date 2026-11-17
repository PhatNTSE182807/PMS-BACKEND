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
        if (roster.Status == "Published" && await db.WorkOpenShifts.AnyAsync(o => o.DutyRosterShiftId == shiftId && o.Status == "Open"))
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

    private async Task<List<DutyStandbyAssignment>> StandbyToReleaseAsync(int lotId, int employeeId, DutyRosterShift shift)
    {
        var rows = await (from s in db.DutyStandbyAssignments join r in db.DutyRosters on s.DutyRosterId equals r.Id
            where !s.IsCancelled && s.EmployeeId == employeeId && r.ParkingLotId == lotId select s).ToListAsync();
        var zone = RotationZone(shift.TimeZoneId);
        return rows.Where(s => DayBounds(s.WorkDate, zone).Start < shift.EndsAt && shift.StartsAt < DayBounds(s.WorkDate, zone).End).ToList();
    }

    private async Task<WorkOpenShift> NewOpenShiftAsync(DutyRosterShift shift, string reason, int? originalId,
        int? leaveId, int? proposedId, List<long> notices)
    {
        var roster = await db.DutyRosters.SingleAsync(r => r.Id == shift.DutyRosterId);
        var needed = await StaffShortageAsync(shift);
        var reserved = await db.WorkOpenShifts.CountAsync(s => s.DutyRosterShiftId == shift.Id && s.Status == "Open");
        if (needed <= reserved) return null;
        var slot = new WorkOpenShift { ParkingLotId = roster.ParkingLotId, DutyRosterShiftId = shift.Id,
            OriginalAssignmentId = originalId, LeaveRequestId = leaveId, ProposedEmployeeId = proposedId,
            Reason = reason, Stage = "Initial", CreatedAt = Now };
        db.WorkOpenShifts.Add(slot); await SaveWorkChangesAsync(); await AdvanceOpenShiftAsync(slot, notices);
        return slot;
    }

    private async Task AdvanceOpenShiftAsync(WorkOpenShift slot, List<long> notices)
    {
        var before = JsonSerializer.Serialize(slot);
        var previousTarget = slot.TargetEmployeeId;
        var shift = await db.DutyRosterShifts.SingleAsync(s => s.Id == slot.DutyRosterShiftId);
        slot.TargetEmployeeId = null;
        if (Now >= shift.EndsAt) { slot.Status = "Expired"; slot.Stage = "Closed"; slot.DeadlineAt = null; }
        else
        {
            if (slot.Stage == "Initial") {
                slot.Stage = "Proposed";
                if (slot.ProposedEmployeeId.HasValue && (await ReplacementIssuesAsync(slot.ParkingLotId, slot.ProposedEmployeeId.Value, shift)).Count == 0)
                    slot.TargetEmployeeId = slot.ProposedEmployeeId;
            }
            else if (slot.Stage == "Proposed") slot.Stage = "Standby";
            else if (slot.Stage == "Standby") slot.Stage = "Public";
            else if (slot.Stage == "Public") slot.Stage = "Manual";
            if (slot.Stage == "Proposed" && !slot.TargetEmployeeId.HasValue) slot.Stage = "Standby";
            if (slot.Stage == "Standby") {
                var people = await (from s in db.DutyStandbyAssignments join r in db.DutyRosters on s.DutyRosterId equals r.Id
                    where !s.IsCancelled && r.ParkingLotId == slot.ParkingLotId && s.WorkDate == shift.WorkDate orderby s.Id select s.EmployeeId).ToListAsync();
                var responded = await db.WorkOpenShiftClaims.Where(c => c.WorkOpenShiftId == slot.Id).Select(c => c.EmployeeId).ToListAsync();
                foreach (var person in people) if (!responded.Contains(person) && (await ReplacementIssuesAsync(slot.ParkingLotId, person, shift)).Count == 0) { slot.TargetEmployeeId = person; break; }
                if (!slot.TargetEmployeeId.HasValue) slot.Stage = "Public";
            }
            var minutes = slot.Stage is "Proposed" or "Standby"
                ? (shift.StartsAt - Now < TimeSpan.FromHours(24) ? RequestRules.EmergencyStandbyMinutes : RequestRules.TargetResponseMinutes)
                : slot.Stage == "Public" ? RequestRules.PublicClaimMinutes : null;
            slot.DeadlineAt = minutes.HasValue ? (Now.AddMinutes(minutes.Value) < shift.EndsAt ? Now.AddMinutes(minutes.Value) : shift.EndsAt) : null;
            if (slot.Stage == "Public" && RequestRules.CutoffHoursBeforeStart.HasValue) {
                var cutoff = shift.StartsAt.AddHours(-RequestRules.CutoffHoursBeforeStart.Value);
                if (Now >= cutoff) { slot.Stage = "Manual"; slot.DeadlineAt = null; }
                else if (!slot.DeadlineAt.HasValue || cutoff < slot.DeadlineAt.Value) slot.DeadlineAt = cutoff;
            }
        }
        slot.Revision++;
        var recipients = new List<string> { await RequestManagerAsync(slot.ParkingLotId) };
        if (previousTarget.HasValue && previousTarget != slot.TargetEmployeeId)
            recipients.Add(await db.Employees.Where(e => e.Id == previousTarget).Select(e => e.UserId).SingleAsync());
        if (slot.Status == "Expired") recipients.AddRange(await (from c in db.WorkOpenShiftClaims join e in db.Employees on c.EmployeeId equals e.Id
            where c.WorkOpenShiftId == slot.Id && c.Status == "Claimed" select e.UserId).ToListAsync());
        if (slot.TargetEmployeeId.HasValue) recipients.Add(await db.Employees.Where(e => e.Id == slot.TargetEmployeeId).Select(e => e.UserId).SingleAsync());
        else if (slot.Stage == "Public") recipients.AddRange((await EligibleReplacementsAsync(slot.ParkingLotId, shift)).Select(e => e.UserId));
        await WorkflowEventAsync(slot.ParkingLotId, "OpenShift", slot.Id, slot.Status == "Open" ? $"Offered:{slot.Stage}" : slot.Status,
            before, slot, slot.Reason, recipients, notices);
    }

    public async Task<WorkOpenShift> CreateOpenShiftAsync(int lotId, CreateOpenShiftInput input)
    {
        await EnsureManagerAsync(lotId);
        return await WorkflowAsync(lotId, async notices => {
            var shift = await db.DutyRosterShifts.SingleOrDefaultAsync(s => s.Id == input.ShiftId && !s.IsCancelled) ?? throw new NotFoundException("Shift not found.");
            var roster = await FindRosterAsync(lotId, shift.DutyRosterId);
            if (roster.Status != "Published" || Now >= shift.EndsAt) throw new BadRequestException("Choose a Published shift that has not ended.");
            var snapshots = await RequestSnapshotsAsync([roster.Id]);
            var slot = await NewOpenShiftAsync(shift, RequestReason(input.Reason), null, null, null, notices)
                ?? throw new BadRequestException("This shift already has enough staff or open vacancies.");
            await TouchRequestRostersAsync(snapshots, "OpenShiftCreated", input.Reason); return slot;
        });
    }

    public async Task<WorkOpenShift> ActOnOpenShiftAsync(int id, WorkRequestCommand input)
    {
        if (input.Reason?.Length > 1000) throw new BadRequestException("Reason must be at most 1000 characters.");
        var initial = await db.WorkOpenShifts.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id) ?? throw new NotFoundException("Open shift not found.");
        return await WorkflowAsync(initial.ParkingLotId, async notices => {
            var slot = await db.WorkOpenShifts.SingleAsync(s => s.Id == id); CheckRevision(slot.Revision, input.Revision);
            var shift = await db.DutyRosterShifts.SingleAsync(s => s.Id == slot.DutyRosterShiftId);
            var roster = await FindRosterAsync(slot.ParkingLotId, shift.DutyRosterId);
            if (slot.Status != "Open" || roster.Status != "Published" || shift.IsCancelled || Now >= shift.EndsAt)
                throw new ConflictException("This open shift is no longer available.");
            var before = JsonSerializer.Serialize(slot);
            if (input.Action is "Claim" or "Decline" or "Withdraw")
            {
                var person = await RequestActorAsync(slot.ParkingLotId);
                if (input.Action != "Withdraw" && ((slot.TargetEmployeeId.HasValue && slot.TargetEmployeeId != person.Id)
                    || (slot.Stage is not ("Proposed" or "Standby" or "Public")))) throw new ForbiddenException("This offer is not open to you.");
                if (slot.DeadlineAt.HasValue && Now >= slot.DeadlineAt && input.Action != "Withdraw") throw new ConflictException("The response window expired; refresh the offer.");
                var claim = await db.WorkOpenShiftClaims.SingleOrDefaultAsync(c => c.WorkOpenShiftId == id && c.EmployeeId == person.Id);
                if (input.Action == "Claim") {
                    await EnsureReplacementAsync(slot.ParkingLotId, person.Id, shift);
                    if (claim != null) throw new ConflictException("You already responded to this offer.");
                    db.WorkOpenShiftClaims.Add(new WorkOpenShiftClaim { WorkOpenShiftId = id, EmployeeId = person.Id, CreatedAt = Now });
                    if (slot.TargetEmployeeId.HasValue) slot.DeadlineAt = null;
                }
                else if (input.Action == "Withdraw") {
                    if (claim?.Status != "Claimed") throw new BadRequestException("No pending claim to withdraw.");
                    claim.Status = "Withdrawn";
                    if (slot.TargetEmployeeId.HasValue) await AdvanceOpenShiftAsync(slot, notices);
                }
                else {
                    if (claim != null) throw new ConflictException("You already responded to this offer.");
                    db.WorkOpenShiftClaims.Add(new WorkOpenShiftClaim { WorkOpenShiftId = id, EmployeeId = person.Id, Status = "Declined", CreatedAt = Now });
                    if (slot.TargetEmployeeId.HasValue) await AdvanceOpenShiftAsync(slot, notices);
                }
                slot.Revision++;
                await WorkflowEventAsync(slot.ParkingLotId, "OpenShift", id, input.Action, before, slot, input.Reason,
                    [person.UserId, await RequestManagerAsync(slot.ParkingLotId)], notices); return slot;
            }
            await EnsureManagerAsync(slot.ParkingLotId); var reason = RequestReason(input.Reason);
            if (input.Action == "RejectClaim") {
                var claim = await db.WorkOpenShiftClaims.SingleOrDefaultAsync(c => c.Id == input.ClaimId && c.WorkOpenShiftId == id && c.Status == "Claimed")
                    ?? throw new BadRequestException("Pending claim not found.");
                claim.Status = "Rejected";
                if (slot.TargetEmployeeId.HasValue) await AdvanceOpenShiftAsync(slot, notices);
                slot.Revision++;
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
            if (input.Action is not ("Confirm" or "Assign" or "ManagerCover")) throw new BadRequestException("Unknown open-shift action.");
            if ((input.Action is "Assign" or "ManagerCover") && slot.Stage == "Public" && slot.DeadlineAt.HasValue && Now >= slot.DeadlineAt.Value)
                await AdvanceOpenShiftAsync(slot, notices);
            if ((input.Action is "Assign" or "ManagerCover") && slot.Stage != "Manual") throw new BadRequestException("Direct assignment follows the proposal, standby and public response windows.");
            if (await StaffShortageAsync(shift) == 0) throw new ConflictException("This shift already has enough staff. Close the vacancy.");
            int? employeeId = input.EmployeeId;
            if (input.Action == "Confirm") employeeId = (await db.WorkOpenShiftClaims.SingleOrDefaultAsync(c => c.Id == input.ClaimId && c.WorkOpenShiftId == id && c.Status == "Claimed"))?.EmployeeId
                ?? throw new BadRequestException("Choose a pending claim to confirm.");
            var standby = new List<DutyStandbyAssignment>();
            if (input.Action != "ManagerCover") {
                if (!employeeId.HasValue) throw new BadRequestException("Choose Staff.");
                await EnsureReplacementAsync(slot.ParkingLotId, employeeId.Value, shift, overtime: input.Action == "Assign" && input.AllowWeeklyOvertime);
                standby = await StandbyToReleaseAsync(slot.ParkingLotId, employeeId.Value, shift);
            }
            var snapshots = await RequestSnapshotsAsync(standby.Select(s => s.DutyRosterId).Append(roster.Id));
            foreach (var row in standby) { row.IsCancelled = true; row.WasCalled = true; row.Source = "Manual"; }
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
            slot.Status = "Filled"; slot.Revision++; slot.DeadlineAt = null;
            foreach (var claim in await db.WorkOpenShiftClaims.Where(c => c.WorkOpenShiftId == id && c.Status == "Claimed").ToListAsync()) {
                claim.Status = input.Action == "Confirm" && claim.Id == input.ClaimId ? "Confirmed" : "Rejected";
                users.Add(await db.Employees.Where(e => e.Id == claim.EmployeeId).Select(e => e.UserId).SingleAsync());
            }
            await TouchRequestRostersAsync(snapshots, "ReplacementConfirmed", reason);
            await WorkflowEventAsync(slot.ParkingLotId, "OpenShift", id, input.Action == "ManagerCover" ? "ManagerCovered" : "ReplacementConfirmed", before, slot, reason, users, notices);
            return slot;
        });
    }

    private async Task RejectRemainingClaimsAsync(int slotId, string status)
    {
        foreach (var claim in await db.WorkOpenShiftClaims.Where(c => c.WorkOpenShiftId == slotId && c.Status == "Claimed").ToListAsync()) claim.Status = status;
    }

    private async Task<List<long>> StopOffersForReopenAsync(int assignmentId, string reason)
    {
        var notices = new List<long>();
        foreach (var slot in await db.WorkOpenShifts.Where(s => s.OriginalAssignmentId == assignmentId && s.Status == "Open").ToListAsync()) {
            var before = JsonSerializer.Serialize(slot); slot.Status = "Closed"; slot.Revision++; slot.DeadlineAt = null;
            var users = await (from c in db.WorkOpenShiftClaims join e in db.Employees on c.EmployeeId equals e.Id
                where c.WorkOpenShiftId == slot.Id && c.Status == "Claimed" select e.UserId).ToListAsync();
            await RejectRemainingClaimsAsync(slot.Id, "Rejected");
            await WorkflowEventAsync(slot.ParkingLotId, "OpenShift", slot.Id, "OriginalShiftReopened", before, slot, reason, users, notices);
        }
        return notices;
    }
}
