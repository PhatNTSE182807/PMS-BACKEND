namespace PMS.Application.Models.WorkScheduling;

public class WorkShiftTemplateInput
{
    public string Name { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int MinimumEntryStaff { get; set; }
    public int MinimumExitStaff { get; set; }
    public int MinimumCounterStaff { get; set; }
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
    public string Position { get; set; }
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
public record AssignmentModel(int Id, int EmployeeId, string StaffName, string Position, string Notes = null)
{
    public string Status => "Assigned";
    public AttendanceModel Attendance { get; init; }
}
public record ShortageModel(int ShiftId, DateOnly WorkDate, string ShiftName, string Position, int Required, int Assigned);
public record RosterShiftModel(int Id, int WorkShiftTemplateId, DateOnly WorkDate, string Name,
    string TimeZoneId, TimeOnly StartTime, TimeOnly EndTime, DateTime StartsAt, DateTime EndsAt,
    int MinimumEntryStaff, int MinimumExitStaff, int MinimumCounterStaff, IReadOnlyList<AssignmentModel> Assignments)
{
    // Captured before filtering personal schedules: own assignments must not be mistaken for the whole shift.
    public int EntryAssigned { get; init; }
    public int ExitAssigned { get; init; }
    public int CounterAssigned { get; init; }
}
public record DutyRosterModel(int Id, int ParkingLotId, string Name, DateOnly FromDate, DateOnly ToDate,
    string Status, int Revision, DateTime? PublishedAt, IReadOnlyList<RosterShiftModel> Shifts, IReadOnlyList<ShortageModel> Shortages);
