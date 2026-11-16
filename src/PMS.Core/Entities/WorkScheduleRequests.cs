namespace PMS.DataAccess.Persistence.Entities;

public class WorkLeaveRequest
{
    public int Id { get; set; }
    public int ParkingLotId { get; set; }
    public int DutyAssignmentId { get; set; }
    public int EmployeeId { get; set; }
    public int? ProposedEmployeeId { get; set; }
    public string Kind { get; set; }
    public string Reason { get; set; }
    public string Status { get; set; } = "Pending";
    public bool IsPaid { get; set; }
    public DateOnly LeaveDate { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public int Revision { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string DecisionReason { get; set; }
}
public class WorkLeaveBalance
{
    public int Id { get; set; }
    public string UserId { get; set; }
    public int Year { get; set; }
    public int EntitledDays { get; set; } = 12;
    public int Revision { get; set; } = 1;
}
public class WorkLeaveDay
{
    public int Id { get; set; }
    public string UserId { get; set; }
    public DateOnly LeaveDate { get; set; }
    public bool IsPaid { get; set; }
    public int FirstLeaveRequestId { get; set; }
}
public class WorkSwapRequest
{
    public int Id { get; set; }
    public int ParkingLotId { get; set; }
    public int AssignmentId { get; set; }
    public int OtherAssignmentId { get; set; }
    public int EmployeeId { get; set; }
    public int OtherEmployeeId { get; set; }
    public string Reason { get; set; }
    public string Status { get; set; } = "AwaitingColleague";
    public int Revision { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public string DecisionReason { get; set; }
}
public class WorkOpenShift
{
    public int Id { get; set; }
    public int ParkingLotId { get; set; }
    public int DutyRosterShiftId { get; set; }
    public int? OriginalAssignmentId { get; set; }
    public int? LeaveRequestId { get; set; }
    public int? ProposedEmployeeId { get; set; }
    public int? TargetEmployeeId { get; set; }
    public string Position { get; set; }
    public string Reason { get; set; }
    public string Status { get; set; } = "Open";
    public string Stage { get; set; } = "Proposed";
    public DateTime? DeadlineAt { get; set; }
    public int Revision { get; set; } = 1;
    public int? ReplacementAssignmentId { get; set; }
    public DateTime CreatedAt { get; set; }
}
public class WorkOpenShiftClaim
{
    public int Id { get; set; }
    public int WorkOpenShiftId { get; set; }
    public int EmployeeId { get; set; }
    public string Status { get; set; } = "Claimed";
    public DateTime CreatedAt { get; set; }
}
public class WorkManagerCoverage
{
    public int Id { get; set; }
    public int WorkOpenShiftId { get; set; }
    public int DutyRosterShiftId { get; set; }
    public string ManagerUserId { get; set; }
    public string Position { get; set; }
    public string Reason { get; set; }
    public DateTime CreatedAt { get; set; }
}
public class WorkScheduleWorkflowEvent
{
    public long Id { get; set; }
    public int ParkingLotId { get; set; }
    public string Kind { get; set; }
    public int EntityId { get; set; }
    public string Action { get; set; }
    public string ActorId { get; set; }
    public string Reason { get; set; }
    public string BeforeJson { get; set; }
    public string AfterJson { get; set; }
    public DateTime CreatedAt { get; set; }
}
