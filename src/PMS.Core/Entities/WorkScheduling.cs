namespace PMS.DataAccess.Persistence.Entities;

// Independent of the legacy Shift tables and of cash drawers.
public class WorkShiftTemplate
{
    public int Id { get; set; }
    public int ParkingLotId { get; set; }
    public string Name { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int MinimumEntryStaff { get; set; }
    public int MinimumExitStaff { get; set; }
    public int MinimumCounterStaff { get; set; }
    public bool IsActive { get; set; } = true;
    public int Revision { get; set; } = 1;
}

public class DutyRoster
{
    public bool HasRotation { get; set; }
    public string RotationConfigurationJson { get; set; }
    public int Id { get; set; }
    public int ParkingLotId { get; set; }
    public string Name { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    public string Status { get; set; } = "Draft";
    public int Revision { get; set; } = 1;
    public string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
}

/// <summary>A dated shift, including unfilled positions. All template values are snapshots.</summary>
public class DutyRosterShift
{
    public int Id { get; set; }
    public int DutyRosterId { get; set; }
    public int WorkShiftTemplateId { get; set; }
    public DateOnly WorkDate { get; set; }
    public string Name { get; set; }
    public string TimeZoneId { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public int MinimumEntryStaff { get; set; }
    public int MinimumExitStaff { get; set; }
    public int MinimumCounterStaff { get; set; }
    public bool IsCancelled { get; set; }
}

public class DutyAssignment
{
    public int? OriginalAssignmentId { get; set; }
    public string ReplacementReason { get; set; }
    public bool AllowWeeklyOvertime { get; set; }
    public string CancellationKind { get; set; }
    public string Source { get; set; } = "Manual";
    public bool IsManuallyEdited { get; set; }
    public int? RotationGroupId { get; set; }
    public string GenerationKey { get; set; }
    public int Id { get; set; }
    public int DutyRosterShiftId { get; set; }
    public int EmployeeId { get; set; }
    public string Position { get; set; }
    public string Notes { get; set; }
    public DateTime AttendanceTrackingFrom { get; set; } = DateTime.UtcNow;
    public bool IsCancelled { get; set; }
}

public class DutyAttendance
{
    public int? ReviewedRevision { get; set; }
    public int Id { get; set; }
    public int DutyAssignmentId { get; set; }
    public DateTime? CheckInAt { get; set; }
    public DateTime? CheckOutAt { get; set; }
    public bool IsAbsent { get; set; }
    public bool IsLate { get; set; }
    public bool AutoEnded { get; set; }
    public int Revision { get; set; }
}

public class DutyAttendanceEvent
{
    public long Id { get; set; }
    public int DutyAssignmentId { get; set; }
    public int Revision { get; set; }
    public string Action { get; set; }
    public string ActorId { get; set; }
    public string Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    public string BeforeJson { get; set; }
    public string AfterJson { get; set; }
}

public class DutyRosterChangeLog
{
    public long Id { get; set; }
    public int DutyRosterId { get; set; }
    public int Revision { get; set; }
    public string Action { get; set; }
    public string ChangedBy { get; set; }
    public DateTime ChangedAt { get; set; }
    public string Reason { get; set; }
    public string BeforeJson { get; set; }
    public string AfterJson { get; set; }
}
