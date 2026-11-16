namespace PMS.DataAccess.Persistence.Entities;

public class AttendancePeriod
{
    public int Id { get; set; }
    public string Name { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    public string Status { get; set; } = "Open";
    public int Revision { get; set; } = 1;
    public string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public string LockedBy { get; set; }
    public DateTime? LockedAt { get; set; }
}

public class AttendancePeriodLot
{
    public int Id { get; set; }
    public int AttendancePeriodId { get; set; }
    public int ParkingLotId { get; set; }
    public string TimeZoneId { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public string Status { get; set; } = "Open";
    public int Revision { get; set; } = 1;
    public string ConfirmedBy { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public string SnapshotJson { get; set; }
}

// Decisions reference an attendance revision and the planned interval. Later edits make them stale.
public class AttendanceOvertimeDecision
{
    public string CalculationKey { get; set; }
    public int Id { get; set; }
    public int DutyAssignmentId { get; set; }
    public int AttendanceRevision { get; set; }
    public DateTime PlannedStart { get; set; }
    public DateTime PlannedEnd { get; set; }
    public bool Approved { get; set; }
    public string Reason { get; set; }
    public string DecidedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ManagerWorkDay
{
    public int Id { get; set; }
    public int ParkingLotId { get; set; }
    public string ManagerUserId { get; set; }
    public DateOnly WorkDate { get; set; }
    public string Kind { get; set; } // Work / Leave. No inferred hours or salary.
    public string Reason { get; set; }
    public int Revision { get; set; } = 1;
}

// A separate approved adjustment in an open subsequent period; never edits the locked source.
public class AttendanceCarryAdjustment
{
    public int Id { get; set; }
    public int SourcePeriodLotId { get; set; }
    public int TargetPeriodLotId { get; set; }
    public int EmployeeId { get; set; }
    public int? DutyAssignmentId { get; set; }
    public decimal Hours { get; set; }
    public string Reason { get; set; }
    public string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AttendancePeriodEvent
{
    public long Id { get; set; }
    public int AttendancePeriodLotId { get; set; }
    public string Action { get; set; }
    public string ActorId { get; set; }
    public string Reason { get; set; }
    public string BeforeJson { get; set; }
    public string AfterJson { get; set; }
    public DateTime CreatedAt { get; set; }
}
