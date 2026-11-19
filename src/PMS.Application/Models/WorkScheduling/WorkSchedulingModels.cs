namespace PMS.Application.Models.WorkScheduling;

public class WorkShiftTemplateInput
{
    public string Name { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int MinimumStaff { get; set; }
    public bool IsActive { get; set; } = true;
    public int Revision { get; set; }
}

public class CreateDutyRosterInput
{
    public string Name { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
}

public class RosterMutationInput
{
    public int Revision { get; set; }
    public string Reason { get; set; }
}

public class RosterShiftInput : RosterMutationInput
{
    public int WorkShiftTemplateId { get; set; }
    public DateOnly WorkDate { get; set; }
}

public class DutyAssignmentInput : RosterMutationInput
{
    public int EmployeeId { get; set; }
    public string Notes { get; set; }
    public int? TargetShiftId { get; set; }
}

public class CalendarAssignmentInput : DutyAssignmentInput
{
    public int? ShiftId { get; set; }
    public int? WorkShiftTemplateId { get; set; }
    public DateOnly WorkDate { get; set; }
}

public record RosterStaffModel(int EmployeeId, string UserId, string FullName, string EmployeeCode);
public record RosterChangeModel(long Id, int DutyRosterId, int Revision, string Action, string ChangedBy,
    string ChangedByName, DateTime ChangedAt, string Reason, string BeforeJson, string AfterJson);
public record AssignmentModel(int Id, int EmployeeId, string StaffName, string Notes = null)
{
    public string Source { get; init; } = "Manual";
    public bool IsManuallyEdited { get; init; }
    public int? OriginalAssignmentId { get; init; }
    public string ReplacementReason { get; init; }
    public string RequestState { get; init; }
    public bool AllowWeeklyOvertime { get; init; }
    public string Status => "Assigned";
    public AttendanceModel Attendance { get; init; }
}
public record ShortageModel(int ShiftId, DateOnly WorkDate, string ShiftName, int Required, int Assigned);
public record RosterShiftModel(int Id, int WorkShiftTemplateId, DateOnly WorkDate, string Name,
    string TimeZoneId, TimeOnly StartTime, TimeOnly EndTime, DateTime StartsAt, DateTime EndsAt,
    int MinimumStaff, IReadOnlyList<AssignmentModel> Assignments)
{
    public bool AttendanceLocked { get; init; }
    // Captured before filtering personal schedules: own assignments must not be mistaken for the whole shift.
    // Staff counted toward the minimum: assignments not marked absent plus recorded Manager coverage.
    public int Assigned { get; init; }
    public IReadOnlyList<AssignmentTrailModel> Trail { get; init; } = [];
    public IReadOnlyList<OpenShiftModel> OpenShifts { get; init; } = [];
    public IReadOnlyList<CoverageModel> ManagerCoverage { get; init; } = [];
}
public record DutyRosterModel(int Id, int ParkingLotId, string Name, DateOnly FromDate, DateOnly ToDate,
    string Status, int Revision, DateTime? PublishedAt, IReadOnlyList<RosterShiftModel> Shifts, IReadOnlyList<ShortageModel> Shortages)
{
    public IReadOnlyList<DateOnly> AttendanceLockedDates { get; init; } = [];
    public IReadOnlyList<RotationIssue> RotationIssues { get; init; } = [];
    public string RotationConfigurationJson { get; init; }
}
