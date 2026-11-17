using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Models.WorkScheduling;
public record PayrollInterval(DateTime Start, DateTime End, string Kind);
public record PayrollPerson(string UserId, int? EmployeeId, string Name, string Role);
public record PayrollAssignmentEvidence(int AssignmentId, int EmployeeId, string UserId, string StaffName,
    string ShiftName, DateOnly WorkDate, string TimeZoneId, DateTime PlannedStart, DateTime PlannedEnd,
    int AttendanceRevision, DateTime? ActualStart, DateTime? ActualEnd, bool Late, bool AutoEnded,
    string AttendanceStatus, IReadOnlyList<PayrollInterval> Intervals, int? LeaveRequestId, bool IsPaidLeave,
    string AttendanceHistoryJson, string OvertimeHistoryJson)
{
    public int RosterId { get; init; }
    public string RosterName { get; init; }
    public int RosterRevision { get; init; }
    public RosterShiftModel ShiftSnapshot { get; init; }
    public AttendanceModel Attendance { get; init; }
}
public record PayrollHoliday(DateOnly Date, string Name);
public record PayrollEvidence(int Version, int PeriodLotId, int LotId, string LotName, string TimeZoneId,
    DateOnly FromDate, DateOnly ToDate, DateTime StartsAt, DateTime EndsAt,
    IReadOnlyList<PayrollPerson> People, IReadOnlyList<PayrollAssignmentEvidence> Assignments,
    IReadOnlyList<ManagerWorkDayModel> ManagerDays,
    IReadOnlyList<PayrollHoliday> Holidays, IReadOnlyList<AttendanceCarryAdjustment> CarryAdjustments);
public class PayrollProfileInput : PayrollProfile { }
public class PayrollGenerateInput { public DateOnly PayMonth { get; set; } public int ParkingLotId { get; set; } public string Role { get; set; } public string UserId { get; set; } public string Reason { get; set; } }
public class PayrollActionInput
{
    public int Revision { get; set; }
    public string Reason { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public string TransactionReference { get; set; }
}
public class PayrollAdjustmentInput
{
    public string UserId { get; set; }
    public string Role { get; set; }
    public int ParkingLotId { get; set; }
    public DateOnly PayMonth { get; set; }
    public int? SourceSlipId { get; set; }
    public int? SourceComplaintId { get; set; }
    public int? SourceCarryId { get; set; }
    public string Kind { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitRate { get; set; }
    public string Reason { get; set; }
}
public class PayrollComplaintInput { public string Reason { get; set; } }
public class PayrollComplaintDecisionInput
{
    public int Revision { get; set; }
    public string Action { get; set; }
    public string Reason { get; set; }
    public PayrollAdjustmentInput Adjustment { get; set; }
}
public record PayrollSlipDetail(PayrollSlip Slip, IReadOnlyList<PayrollLine> Lines, IReadOnlyList<PayrollComplaint> Complaints,
    IReadOnlyList<PayrollAudit> History, bool Stale);
public record PayrollCalculation(string UserId, int? EmployeeId, string EmployeeName, string Role,
    string Currency, decimal Total, IReadOnlyList<PayrollLine> Lines, string BasisJson, string BasisHash, IReadOnlyList<int> SourceIds);
public record PayrollSourceBundle(int Id, string Hash, PayrollEvidence Evidence);
public record PayrollSourceModel(int Id, int PeriodLotId, int LotId, DateOnly FromDate, DateOnly ToDate, bool Sealed,
    string Hash, string Error);
