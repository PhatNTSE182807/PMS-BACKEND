using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.WorkScheduling;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;
public partial class WorkSchedulingService
{
    private static void PayrollUtc(PayrollSlip slip)
    {
        slip.CreatedAt = Utc(slip.CreatedAt);
        if (slip.ApprovedAt.HasValue) slip.ApprovedAt = Utc(slip.ApprovedAt.Value);
        if (slip.PaidAt.HasValue) slip.PaidAt = Utc(slip.PaidAt.Value);
    }
    private static void PayrollUtc(PayrollLine line)
    {
        if (line.StartsAt.HasValue) line.StartsAt = Utc(line.StartsAt.Value);
        if (line.EndsAt.HasValue) line.EndsAt = Utc(line.EndsAt.Value);
    }
    private async Task EnsurePayrollReadAsync(PayrollSlip slip)
    {
        if (scope.Role == "Admin") return;
        if (slip.Status == "Draft") throw new ForbiddenException("Only Admin can view payroll drafts.");
        if (slip.UserId == scope.UserId && scope.Role is "Staff" or "Manager") return;
        if (scope.Role == "Manager" && slip.Role == "Staff") { await EnsureManagerAsync(slip.ParkingLotId); return; }
        throw new ForbiddenException("You cannot access this payslip.");
    }
    public async Task<IReadOnlyList<PayrollSlip>> PayrollSlipsAsync(int? lotId, DateOnly? month, bool mine = false)
    {
        if (scope.Role is not ("Admin" or "Manager" or "Staff")) throw new ForbiddenException("Payroll access denied.");
        var query = db.PayrollSlips.AsNoTracking().AsQueryable();
        if (mine) query = query.Where(s => s.UserId == scope.UserId);
        if (lotId.HasValue) {
            if (scope.Role == "Manager") await EnsureManagerAsync(lotId.Value);
            else if (scope.Role == "Admin") await scope.EnsureCanManageAsync(lotId.Value);
            query = query.Where(s => s.ParkingLotId == lotId);
        }
        if (month.HasValue) { PayrollMonth(month.Value); query = query.Where(s => s.PayMonth == month); }
        if (scope.Role == "Staff") query = query.Where(s => s.UserId == scope.UserId && s.Status != "Draft");
        if (scope.Role == "Manager") {
            var managed = await db.ParkingLots.Where(l => l.ManagerUserId == scope.UserId && !l.IsDeleted).Select(l => l.Id).ToListAsync();
            query = query.Where(s => s.Status != "Draft" && (s.UserId == scope.UserId || (s.Role == "Staff" && managed.Contains(s.ParkingLotId))));
        }
        var rows = await query.OrderByDescending(s => s.PayMonth).ThenBy(s => s.EmployeeName).Take(500).ToListAsync();
        foreach (var row in rows) PayrollUtc(row);
        PayrollAuditEntry("PayslipsViewed", lotId, null, null, null, new { SlipIds = rows.Select(s => s.Id).ToArray() }); await SaveWorkChangesAsync(); return rows;
    }
    private async Task<bool> PayrollStaleAsync(PayrollSlip slip)
    {
        if (slip.Status != "Draft") return false;
        try {
            var plans = await CalculatePayrollAsync(new() { PayMonth = slip.PayMonth, ParkingLotId = slip.ParkingLotId, Role = slip.Role }, slip.UserId);
            return plans.SingleOrDefault(p => p.UserId == slip.UserId)?.BasisHash != slip.BasisHash;
        } catch (BadRequestException) { return true; } catch (ConflictException) { return true; }
    }
    public async Task<PayrollSlipDetail> PayrollSlipAsync(int id)
    {
        var slip = await db.PayrollSlips.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id) ?? throw new NotFoundException("Payslip not found.");
        await EnsurePayrollReadAsync(slip);
        var lines = await db.PayrollLines.AsNoTracking().Where(l => l.PayrollSlipId == id).OrderBy(l => l.Ordinal).ToListAsync();
        PayrollUtc(slip); foreach (var line in lines) PayrollUtc(line);
        var complaints = await db.PayrollComplaints.AsNoTracking().Where(c => c.PayrollSlipId == id).OrderByDescending(c => c.Id).ToListAsync();
        PayrollAuditEntry("PayslipViewed", slip.ParkingLotId, id, null, null, new { slip.Revision }); await SaveWorkChangesAsync();
        var history = scope.Role == "Admin" ? await db.PayrollAudits.AsNoTracking().Where(a => a.PayrollSlipId == id).OrderByDescending(a => a.Id).Take(200).ToListAsync() : new List<PayrollAudit>();
        return new(slip, lines, complaints, history, scope.Role == "Admin" && await PayrollStaleAsync(slip));
    }
    private async Task<List<long>> PayrollNotifyAsync(PayrollSlip slip, string action, string message)
    {
        var users = new HashSet<string> { slip.UserId };
        if (slip.Role == "Staff") {
            var manager = await db.ParkingLots.Where(l => l.Id == slip.ParkingLotId).Select(l => l.ManagerUserId).SingleAsync();
            if (manager != null) users.Add(manager);
        }
        var notices = users.Select(u => new UserNotification { UserId = u, Type = "PayrollChanged", Title = $"Payslip {action}", Message = message,
            RefType = "PayrollSlip", RefId = $"{slip.Id}:{slip.Revision}:{action}", EventAt = Now, CreatedAt = Now,
            DataJson = JsonSerializer.Serialize(new { slipId = slip.Id, lotId = slip.ParkingLotId }) }).ToList();
        db.UserNotifications.AddRange(notices); await SaveWorkChangesAsync(); return notices.Select(n => n.Id).ToList();
    }
    public async Task<PayrollSlipDetail> ActOnPayrollAsync(int id, string action, PayrollActionInput input)
    {
        EnsurePeriodAdmin(); var reason = RequestReason(input.Reason); var notices = new List<long>();
        var lotId = await WriteAsync(async () => {
            var slip = await db.PayrollSlips.SingleOrDefaultAsync(s => s.Id == id) ?? throw new NotFoundException("Payslip not found.");
            CheckRevision(slip.Revision, input.Revision);
            var before = JsonSerializer.Serialize(slip);
            if (action == "approve") {
                if (slip.Status != "Draft") throw new ConflictException("Only Draft payroll can be approved.");
                if (await PayrollStaleAsync(slip)) throw new ConflictException("Salary inputs changed. Recalculate and review the Draft before approval.");
                slip.Status = "Approved"; slip.ApprovedAt = Now;
            } else if (action == "paid") {
                if (slip.Status != "Approved") throw new ConflictException("Only Approved payroll can be marked Paid; Paid records are immutable.");
                var transaction = input.TransactionReference?.Trim();
                if (!input.PaidAt.HasValue || input.PaidAt > new DateTimeOffset(Now, TimeSpan.Zero) || input.PaidAt.Value.Year < 1900
                    || string.IsNullOrWhiteSpace(transaction) || transaction.Length > 200 || slip.Total < 0)
                    throw new BadRequestException("Enter the actual external payment date (not in the future), a transaction reference, and a non-negative payment total.");
                slip.Status = "Paid"; slip.PaidAt = input.PaidAt.Value.UtcDateTime; slip.TransactionReference = transaction;
            } else throw new BadRequestException("Unknown payroll action.");
            slip.Revision++; PayrollAuditEntry(action == "approve" ? "Approved" : "Paid", slip.ParkingLotId, id, reason, before, slip);
            notices.AddRange(await PayrollNotifyAsync(slip, slip.Status, $"{slip.PayMonth:yyyy-MM} · {slip.EmployeeName}. Open the payslip to review its frozen calculation."));
            return slip.ParkingLotId;
        });
        QueueAttendancePush(lotId, notices); return await PayrollSlipAsync(id);
    }

    private async Task<PayrollAdjustment> CreatePayrollAdjustmentRowAsync(PayrollAdjustmentInput input)
    {
        var month = PayrollMonth(input.PayMonth); var reason = RequestReason(input.Reason); await scope.EnsureCanManageAsync(input.ParkingLotId);
        if (input.Role is not ("Staff" or "Manager") || input.Kind is not ("Adjustment" or "Allowance" or "Bonus")
            || input.Quantity == 0 || input.UnitRate < 0 || input.UnitRate > 1000000000m || Math.Abs(input.Quantity) > 1000000m
            || decimal.Round(input.Quantity, 12) != input.Quantity || decimal.Round(input.UnitRate, 6) != input.UnitRate)
            throw new BadRequestException("Enter a non-zero explained adjustment, allowance or bonus with valid quantity and rate.");
        if (input.Role == "Staff" && input.Kind is "Allowance" or "Bonus") throw new BadRequestException("Staff standby allowance is calculated from sealed standby records. Other corrections must use a traced adjustment.");
        if (await identity.Users.AnyAsync(u => u.Id == input.UserId && u.Role == "Admin")) throw new BadRequestException("Admin salary is not managed.");
        if (await db.PayrollSlips.AnyAsync(s => s.UserId == input.UserId && s.ParkingLotId == input.ParkingLotId && s.PayMonth == month && s.Status != "Draft"))
            throw new ConflictException("The target payroll month is approved/paid. Choose a later open month.");
        var sourceSlip = input.SourceSlipId.HasValue ? await db.PayrollSlips.SingleOrDefaultAsync(s => s.Id == input.SourceSlipId) : null;
        if (input.SourceSlipId.HasValue && (sourceSlip == null || sourceSlip.Status == "Draft" || sourceSlip.UserId != input.UserId || sourceSlip.Role != input.Role
            || sourceSlip.ParkingLotId != input.ParkingLotId || sourceSlip.PayMonth >= month))
            throw new BadRequestException("The adjustment source must be this employee's earlier approved/paid payslip at the same lot.");
        if (input.Quantity < 0 && (!input.SourceSlipId.HasValue && !input.SourceCarryId.HasValue))
            throw new BadRequestException("A negative correction requires an earlier payslip or a locked attendance carry source; Late/cash penalties are not payroll entries.");
        if (input.Kind == "Adjustment" && !input.SourceSlipId.HasValue && !input.SourceCarryId.HasValue)
            throw new BadRequestException("A payroll correction must reference an earlier payslip or a locked attendance hours carry.");
        if (input.Kind is "Allowance" or "Bonus" && !await db.ParkingLots.AnyAsync(l => l.Id == input.ParkingLotId && l.ManagerUserId == input.UserId))
            throw new BadRequestException("Manager allowance/bonus entries must belong to this lot's Manager.");
        if (input.SourceCarryId.HasValue) {
            var source = await db.AttendanceCarryAdjustments.AsNoTracking().SingleOrDefaultAsync(c => c.Id == input.SourceCarryId) ?? throw new NotFoundException("Attendance carry not found.");
            var period = await db.AttendancePeriodLots.SingleAsync(p => p.Id == source.TargetPeriodLotId);
            if (period.Status != "Locked" || period.ParkingLotId != input.ParkingLotId || source.Hours != input.Quantity || input.Role != "Staff"
                || !await db.Employees.AnyAsync(e => e.Id == source.EmployeeId && e.UserId == input.UserId))
                throw new BadRequestException("Carry valuation must preserve the locked source employee, lot and exact signed hours.");
            var periodDates = await db.AttendancePeriods.SingleAsync(p => p.Id == period.AttendancePeriodId);
            if (month > periodDates.ToDate || month.AddMonths(1).AddDays(-1) < periodDates.FromDate) throw new BadRequestException("Choose the month of the locked carry target.");
            if (await db.PayrollAdjustments.AnyAsync(a => a.SourceCarryId == source.Id)) throw new ConflictException("This attendance carry already has a payroll valuation.");
        }
        if (input.SourceComplaintId.HasValue && await db.PayrollAdjustments.AnyAsync(a => a.SourceComplaintId == input.SourceComplaintId)) throw new ConflictException("This complaint already has a payroll adjustment.");
        var profileExists = await db.PayrollProfiles.AnyAsync(p => p.UserId == input.UserId && p.Role == input.Role && p.EffectiveFrom <= month.AddMonths(1).AddDays(-1));
        if (!profileExists) throw new BadRequestException("Configure the employee's salary profile before recording payroll adjustments.");
        var row = new PayrollAdjustment { UserId = input.UserId, Role = input.Role, ParkingLotId = input.ParkingLotId, PayMonth = month,
            SourceSlipId = input.SourceSlipId, SourceComplaintId = input.SourceComplaintId, SourceCarryId = input.SourceCarryId,
            Kind = input.Kind, Quantity = input.Quantity, UnitRate = input.UnitRate, Reason = reason, CreatedBy = scope.UserId, CreatedAt = Now };
        db.PayrollAdjustments.Add(row); await SaveWorkChangesAsync(); PayrollAuditEntry("AdjustmentCreated", input.ParkingLotId, sourceSlip?.Id, reason, null, row); return row;
    }
    public async Task<PayrollAdjustment> AddPayrollAdjustmentAsync(PayrollAdjustmentInput input)
    {
        EnsurePeriodAdmin();
        if (input.SourceComplaintId.HasValue) throw new BadRequestException("Accept the complaint through its resolution workflow to attach an adjustment.");
        return await WriteAsync(async () => { var row = await CreatePayrollAdjustmentRowAsync(input); await SaveWorkChangesAsync(); return row; });
    }
    public async Task<IReadOnlyList<PayrollAdjustment>> PayrollAdjustmentsAsync(int lotId, DateOnly month)
    {
        EnsurePeriodAdmin(); await scope.EnsureCanManageAsync(lotId); PayrollMonth(month);
        return await db.PayrollAdjustments.AsNoTracking().Where(a => a.ParkingLotId == lotId && a.PayMonth == month).OrderBy(a => a.Id).ToListAsync();
    }
    public async Task<PayrollComplaint> ComplainPayrollAsync(int slipId, PayrollComplaintInput input)
    {
        var notices = new List<long>();
        var row = await WriteAsync(async () => {
            var slip = await db.PayrollSlips.SingleOrDefaultAsync(s => s.Id == slipId) ?? throw new NotFoundException("Payslip not found.");
            if (scope.Role is not ("Staff" or "Manager") || slip.UserId != scope.UserId || slip.Status == "Draft") throw new ForbiddenException("Only the employee may complain about their published payslip.");
            if (await db.PayrollComplaints.AnyAsync(c => c.PayrollSlipId == slipId && c.Status == "Open")) throw new ConflictException("This payslip already has an open complaint.");
            var complaint = new PayrollComplaint { PayrollSlipId = slipId, UserId = scope.UserId, Reason = RequestReason(input.Reason), CreatedAt = Now };
            db.PayrollComplaints.Add(complaint); await SaveWorkChangesAsync();
            PayrollAuditEntry("ComplaintCreated", slip.ParkingLotId, slipId, complaint.Reason, null, complaint);
            var adminIds = await identity.Users.Where(u => u.Role == "Admin" && u.IsActive).Select(u => u.Id).ToListAsync();
            var notifications = adminIds.Select(u => new UserNotification { UserId = u, Type = "PayrollChanged", Title = "New payroll complaint",
                Message = $"Payslip #{slip.Id} has a new complaint. Open payroll to review the employee's source evidence.",
                RefType = "PayrollSlip", RefId = $"{slip.Id}:complaint:{complaint.Id}", CreatedAt = Now, EventAt = Now,
                DataJson = JsonSerializer.Serialize(new { slipId = slip.Id, lotId = slip.ParkingLotId }) }).ToList();
            db.UserNotifications.AddRange(notifications); await SaveWorkChangesAsync(); notices.AddRange(notifications.Select(n => n.Id)); return (Complaint: complaint, LotId: slip.ParkingLotId);
        });
        QueueAttendancePush(row.LotId, notices); return row.Complaint;
    }
    public async Task<PayrollComplaint> ResolvePayrollComplaintAsync(int id, PayrollComplaintDecisionInput input)
    {
        EnsurePeriodAdmin(); var reason = RequestReason(input.Reason); var notices = new List<long>();
        var result = await WriteAsync(async () => {
            var c = await db.PayrollComplaints.SingleOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("Payroll complaint not found.");
            CheckRevision(c.Revision, input.Revision);
            if (c.Status != "Open") throw new ConflictException("Only an Open complaint can be resolved.");
            var slip = await db.PayrollSlips.SingleAsync(s => s.Id == c.PayrollSlipId); var before = JsonSerializer.Serialize(c);
            if (input.Action == "Accept") {
                if (input.Adjustment == null) throw new BadRequestException("Accepting a complaint requires an explained subsequent-month adjustment.");
                var a = input.Adjustment; a.UserId = slip.UserId; a.Role = slip.Role; a.ParkingLotId = slip.ParkingLotId;
                a.SourceSlipId = slip.Id; a.SourceComplaintId = c.Id; a.Kind = "Adjustment";
                var row = await CreatePayrollAdjustmentRowAsync(a); c.AdjustmentId = row.Id; c.Status = "Accepted";
            } else if (input.Action == "Reject") c.Status = "Rejected";
            else throw new BadRequestException("Choose Accept or Reject.");
            c.Revision++; c.ResolutionReason = reason; c.ResolvedAt = Now;
            PayrollAuditEntry("ComplaintResolved", slip.ParkingLotId, slip.Id, reason, before, c);
            notices.AddRange(await PayrollNotifyAsync(slip, c.Status, $"Payroll complaint #{c.Id}: {c.Status}. The source payslip remains unchanged."));
            return (Complaint: c, LotId: slip.ParkingLotId);
        });
        QueueAttendancePush(result.LotId, notices); return result.Complaint;
    }
    public async Task<object> PayrollLineSourceAsync(int slipId, int lineId)
    {
        var slip = await db.PayrollSlips.AsNoTracking().SingleOrDefaultAsync(s => s.Id == slipId) ?? throw new NotFoundException("Payslip not found.");
        await EnsurePayrollReadAsync(slip);
        var line = await db.PayrollLines.AsNoTracking().SingleOrDefaultAsync(l => l.Id == lineId && l.PayrollSlipId == slipId) ?? throw new NotFoundException("Payslip line not found.");
        PayrollUtc(line);
        PayrollAuditEntry("PayrollSourceViewed", slip.ParkingLotId, slip.Id, null, null, new { lineId }); await SaveWorkChangesAsync();
        using var json = JsonDocument.Parse(line.SourceJson);
        PayrollAssignmentEvidence assignment = null;
        if (json.RootElement.GetProperty("Evidence").TryGetProperty("Assignment", out var value)) assignment = value.Deserialize<PayrollAssignmentEvidence>();
        if (assignment != null) {
            var roster = new DutyRosterModel(assignment.RosterId, slip.ParkingLotId, assignment.RosterName, assignment.WorkDate,
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(assignment.PlannedEnd, DateTimeKind.Utc), PMS.Application.Helpers.Parking.ParkingText.FindTimeZone(assignment.TimeZoneId))),
                "Published", assignment.RosterRevision, null, [assignment.ShiftSnapshot with { AttendanceLocked = true }], []);
            return new { Line = line, Roster = roster, Assignment = assignment, Frozen = true };
        }
        return new { Line = line, Roster = (DutyRosterModel)null, Assignment = assignment, Frozen = true };
    }
    public async Task<object> PayrollAuditAsync(int? lotId)
    {
        EnsurePeriodAdmin();
        return await db.PayrollAudits.AsNoTracking().Where(a => !lotId.HasValue || a.ParkingLotId == lotId).OrderByDescending(a => a.Id).Take(500).ToListAsync();
    }
}
