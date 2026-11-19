namespace PMS.DataAccess.Persistence.Entities;

public class PayrollProfile
{
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string EmployeeName { get; set; }
    public int Id { get; set; }
    public string UserId { get; set; }
    public string Role { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public decimal? HourlyRate { get; set; }
    public decimal? MonthlyRate { get; set; }
    public TimeOnly? NightStart { get; set; }
    public TimeOnly? NightEnd { get; set; }
    public decimal? NightFactor { get; set; }
    public decimal? HolidayFactor { get; set; }
    public decimal? OvertimeFactor { get; set; }
    public string OvertimeCombination { get; set; }
    public string PaidLeaveMethod { get; set; }
    public decimal? PaidLeaveHoursPerDay { get; set; }
    public decimal? StandardWorkDays { get; set; }
    public string Currency { get; set; }
    public int? MoneyDecimals { get; set; }
    public string RoundingMode { get; set; }
    public string Reason { get; set; }
    public int Revision { get; set; } = 1;
    public string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class PayrollAttendanceSource
{
    public int Id { get; set; }
    public int AttendancePeriodLotId { get; set; }
    public string SnapshotJson { get; set; }
    public string SnapshotHash { get; set; }
    public string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class PayrollSlip
{
    public int Id { get; set; }
    public string UserId { get; set; }
    public int? EmployeeId { get; set; }
    public string EmployeeName { get; set; }
    public string Role { get; set; }
    public int ParkingLotId { get; set; }
    public DateOnly PayMonth { get; set; }
    public string Status { get; set; } = "Draft";
    public string Currency { get; set; }
    public decimal Total { get; set; }
    public string BasisHash { get; set; }
    public string BasisJson { get; set; }
    public string SourceIdsJson { get; set; }
    public int Revision { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public string TransactionReference { get; set; }
}

public class PayrollLine
{
    public int Id { get; set; }
    public int PayrollSlipId { get; set; }
    public int Ordinal { get; set; }
    public string Kind { get; set; }
    public string Description { get; set; }
    public int SourcePeriodLotId { get; set; }
    public int? AssignmentId { get; set; }
    public int? AttendanceRevision { get; set; }
    public int? LeaveRequestId { get; set; }
    public int? ManagerDayId { get; set; }
    public int? AdjustmentId { get; set; }
    public int? ProfileId { get; set; }
    public DateOnly WorkDate { get; set; }
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public string Unit { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitRate { get; set; }
    public decimal Factor { get; set; }
    public decimal Amount { get; set; }
    public string SourceJson { get; set; }
}

public class PayrollAdjustment
{
    public int Id { get; set; }
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
    public string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class PayrollComplaint
{
    public int Id { get; set; }
    public int PayrollSlipId { get; set; }
    public string UserId { get; set; }
    public string Reason { get; set; }
    public string Status { get; set; } = "Open";
    public string ResolutionReason { get; set; }
    public int? AdjustmentId { get; set; }
    public int Revision { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

public class PayrollAudit
{
    public long Id { get; set; }
    public string ActorId { get; set; }
    public string Action { get; set; }
    public int? ParkingLotId { get; set; }
    public int? PayrollSlipId { get; set; }
    public string Reason { get; set; }
    public string BeforeJson { get; set; }
    public string AfterJson { get; set; }
    public DateTime CreatedAt { get; set; }
}
