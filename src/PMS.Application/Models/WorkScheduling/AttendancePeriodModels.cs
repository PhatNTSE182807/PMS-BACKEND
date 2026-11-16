using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Models.WorkScheduling;

public class CreateAttendancePeriodInput
{
    public string Name { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    public int[] LotIds { get; set; } = [];
}
public class PeriodActionInput
{
    public int Revision { get; set; }
    public string Reason { get; set; }
    public bool AcknowledgeUnconfirmed { get; set; }
}
public class OvertimeDecisionInput : AttendanceMutationInput { public bool Approved { get; set; } public int DecisionId { get; set; } }
public class ManagerWorkDayInput
{
    public string ManagerUserId { get; set; }
    public DateOnly WorkDate { get; set; }
    public string Kind { get; set; }
    public int Revision { get; set; }
    public string Reason { get; set; }
}
public class CarryAdjustmentInput
{
    public int Revision { get; set; }
    public int SourcePeriodLotId { get; set; }
    public int EmployeeId { get; set; }
    public int? AssignmentId { get; set; }
    public decimal Hours { get; set; }
    public string Reason { get; set; }
}
public record PeriodLotModel(int Id, int ParkingLotId, string LotName, string TimeZoneId, string Status,
    int Revision, DateTime? ConfirmedAt);
public record PeriodModel(int Id, string Name, DateOnly FromDate, DateOnly ToDate, string Status,
    int Revision, DateTime? LockedAt, IReadOnlyList<PeriodLotModel> Lots);
public record AttendancePayReview(DateTime? OriginalCheckInAt, DateTime? OriginalCheckOutAt,
    decimal? OriginalHours, decimal AdjustedHours, decimal RegularHours, decimal OvertimeHours,
    string OvertimeStatus, decimal ApprovedHours, bool HasAdjustments, bool Reviewed, bool Locked,
    IReadOnlyList<string> Periods, int OvertimeDecisionId)
{
    public IReadOnlyList<AttendanceOvertimeDecision> OvertimeHistory { get; init; } = [];
}
public record ManagerWorkDayModel(int Id, int ParkingLotId, string ManagerUserId, string ManagerName,
    DateOnly WorkDate, string Kind, string Reason, int Revision);
public record AttendanceReviewRow(int AssignmentId, int EmployeeId, string StaffName, string ShiftName,
    DateOnly WorkDate, DateTime StartsAt, DateTime EndsAt, AttendanceModel Attendance, AttendancePayReview Pay);
public record AttendanceEmployeeSummary(int EmployeeId, string StaffName, decimal ScheduledHours,
    decimal? ActualHours, decimal AdjustedHours, decimal ApprovedHours, decimal OvertimeHours,
    decimal ApprovedOvertimeHours, int PaidLeaveDays, int UnpaidLeaveDays, int AbsentShifts, decimal CarryHours);
public record AttendancePeriodReview(PeriodModel Period, PeriodLotModel Lot, IReadOnlyList<AttendanceReviewRow> Rows,
    IReadOnlyList<AttendanceEmployeeSummary> Employees, IReadOnlyList<string> Warnings,
    IReadOnlyList<ManagerWorkDayModel> ManagerDays, IReadOnlyList<AttendanceCarryAdjustment> Adjustments,
    IReadOnlyList<AttendancePeriodEvent> History);
