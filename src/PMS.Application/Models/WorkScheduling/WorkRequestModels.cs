namespace PMS.Application.Models.WorkScheduling;

public class WorkRequestOptions
{
    public int PlannedNoticeHours { get; set; } = 24;
    public int AnnualPaidDays { get; set; } = 12;
    public int? TargetResponseMinutes { get; set; } = 120;
    public int EmergencyStandbyMinutes { get; set; } = 60;
    public int? PublicClaimMinutes { get; set; } = 1440;
    public int? CutoffHoursBeforeStart { get; set; } = 24;
    public void Validate()
    {
        if (PlannedNoticeHours < 0 || AnnualPaidDays < 0 || TargetResponseMinutes <= 0 || PublicClaimMinutes <= 0 || EmergencyStandbyMinutes <= 0 || CutoffHoursBeforeStart < 0)
            throw new InvalidOperationException("Invalid work request configuration.");
    }
}
public class CreateLeaveInput
{
    public int AssignmentId { get; set; }
    public string Kind { get; set; }
    public string Reason { get; set; }
    public int? ProposedEmployeeId { get; set; }
}
public class CreateSwapInput
{
    public int AssignmentId { get; set; }
    public int OtherAssignmentId { get; set; }
    public string Reason { get; set; }
}
public class WorkRequestCommand
{
    public int Revision { get; set; }
    public string Action { get; set; }
    public string Reason { get; set; }
    public int? ClaimId { get; set; }
    public int? EmployeeId { get; set; }
    public bool AllowWeeklyOvertime { get; set; }
}
public class CreateOpenShiftInput
{
    public int ShiftId { get; set; }
    public string Reason { get; set; }
}
public class LeaveEntitlementInput
{
    public int Revision { get; set; }
    public int EntitledDays { get; set; }
    public string Reason { get; set; }
}
public record LeaveBalanceModel(int EmployeeId, string StaffName, int Year, int EntitledDays, int PaidDaysUsed, int UnpaidDays, int Revision);
public record LeaveRequestModel(int Id, int AssignmentId, int EmployeeId, string StaffName, string Kind, string Status, int Revision,
    DateOnly LeaveDate, DateTime StartsAt, DateTime EndsAt, string Reason, bool IsPaid, int? ProposedEmployeeId, string DecisionReason);
public record SwapRequestModel(int Id, int AssignmentId, int OtherAssignmentId, int EmployeeId, int OtherEmployeeId,
    string StaffName, string OtherStaffName, string Status, int Revision, string Reason, string DecisionReason);
public record OpenShiftModel(int Id, int ShiftId, string Status, string Stage, int Revision, int? OriginalAssignmentId,
    int? TargetEmployeeId, DateTime? DeadlineAt, int? ReplacementAssignmentId, DateTime StartsAt, DateTime EndsAt, DateOnly WorkDate, string ShiftName);
public record OpenShiftClaimModel(int Id, int EmployeeId, string StaffName, string Status);
public record CoverageModel(int Id, int ShiftId, string ManagerName, string Reason, int OpenShiftId);
public record AssignmentTrailModel(int AssignmentId, int EmployeeId, string StaffName, string Kind, int RequestId, bool IsPaid, string Reason);
public record WorkRequestsModel(IReadOnlyList<LeaveRequestModel> Leaves, IReadOnlyList<SwapRequestModel> Swaps,
    IReadOnlyList<OpenShiftModel> OpenShifts, IReadOnlyList<LeaveBalanceModel> Balances, WorkRequestOptions Options);
public record OpenShiftDetailModel(OpenShiftModel Slot, IReadOnlyList<RosterStaffModel> Candidates, IReadOnlyList<OpenShiftClaimModel> Claims,
    IReadOnlyList<StandbyModel> Standby, IReadOnlyList<WorkflowEventModel> History, IReadOnlyList<RosterStaffModel> Staff);
public record WorkflowEventModel(long Id, string Action, string ActorId, string Reason, DateTime CreatedAt, string BeforeJson, string AfterJson);
public record SwapCandidateModel(int AssignmentId, int EmployeeId, string StaffName, string ShiftName, DateOnly WorkDate, DateTime StartsAt, DateTime EndsAt);
public record AssignmentRequestDetailModel(int AssignmentId, int LotId, int EmployeeId, string StaffName, string Status,
    DateTime StartsAt, DateTime EndsAt, bool CanRequest, IReadOnlyList<RosterStaffModel> Staff,
    IReadOnlyList<SwapCandidateModel> SwapCandidates, IReadOnlyList<LeaveRequestModel> Leaves, IReadOnlyList<SwapRequestModel> Swaps);
