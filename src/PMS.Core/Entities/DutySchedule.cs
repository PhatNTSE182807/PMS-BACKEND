using System;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// One version of a lot's weekly duty pattern: who works each of the 4 daily shifts on each weekday.
/// The pattern repeats every week from <see cref="EffectiveFrom"/> until a later version replaces it.
/// </summary>
public class DutyPattern
{
    public int Id { get; set; }
    public int ParkingLotId { get; set; }
    /// <summary>First local date this version applies to.</summary>
    public DateOnly EffectiveFrom { get; set; }
    public string CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>One filled cell of a pattern version. Empty cells have no row.</summary>
public class DutyPatternCell
{
    public int Id { get; set; }
    public int DutyPatternId { get; set; }
    /// <summary>1 = Monday … 7 = Sunday.</summary>
    public int DayOfWeek { get; set; }
    /// <summary>0 = 00–06, 1 = 06–12, 2 = 12–18, 3 = 18–24.</summary>
    public int Slot { get; set; }
    public int EmployeeId { get; set; }
}

/// <summary>A change to one shift on one date only. <see cref="EmployeeId"/> null leaves the shift empty.</summary>
public class DutyOverride
{
    public int Id { get; set; }
    public int ParkingLotId { get; set; }
    public DateOnly WorkDate { get; set; }
    public int Slot { get; set; }
    public int? EmployeeId { get; set; }
    /// <summary>Set when the change comes from an approved leave request (the person on leave).</summary>
    public int? LeaveRequestId { get; set; }
    public string Reason { get; set; }
    public string CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Check-in / check-out of one Staff member for one shift, or an absence when they never checked in.</summary>
public class DutyShiftAttendance
{
    public long Id { get; set; }
    public int ParkingLotId { get; set; }
    public int EmployeeId { get; set; }
    public DateOnly WorkDate { get; set; }
    public int Slot { get; set; }
    /// <summary>"Working", "Completed" or "Absent".</summary>
    public string Status { get; set; }
    public DateTime? CheckInAt { get; set; }
    public DateTime? CheckOutAt { get; set; }
    /// <summary>The Staff forgot to end the shift; the scheduled end was recorded.</summary>
    public bool IsAutoEnded { get; set; }
    public string AdjustedByUserId { get; set; }
    public string AdjustReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A Staff member's request to be off for a range of local dates.</summary>
public class DutyLeaveRequest
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public int ParkingLotId { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    public string Reason { get; set; }
    /// <summary>"Pending", "Approved", "Rejected" or "Cancelled".</summary>
    public string Status { get; set; }
    public string DecisionReason { get; set; }
    public string DecidedByUserId { get; set; }
    public DateTime? DecidedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>One calendar day of an approved leave request, with whether it is paid.</summary>
public class DutyLeaveDay
{
    public int Id { get; set; }
    public int DutyLeaveRequestId { get; set; }
    public int EmployeeId { get; set; }
    public DateOnly LeaveDate { get; set; }
    /// <summary>The person had at least one shift that day when the leave was approved.</summary>
    public bool HadShift { get; set; }
    /// <summary>Taken from the yearly paid allowance; only meaningful when <see cref="HadShift"/>.</summary>
    public bool IsPaid { get; set; }
}

/// <summary>Paid leave days a Staff member gets per year (12 by default).</summary>
public class WorkLeaveBalance
{
    public int Id { get; set; }
    public string UserId { get; set; }
    public int Year { get; set; }
    public int EntitledDays { get; set; } = 12;
    public int Revision { get; set; } = 1;
}

/// <summary>A monthly payslip, or a simulated one a Staff / Manager created for themselves.</summary>
public class PaySlip
{
    public long Id { get; set; }
    public int EmployeeId { get; set; }
    public string UserId { get; set; }
    public int? ParkingLotId { get; set; }
    /// <summary>First day of the pay month.</summary>
    public DateOnly Month { get; set; }
    /// <summary>"FullTime", "Backup" or "Manager".</summary>
    public string Kind { get; set; }
    public decimal BaseAmount { get; set; }
    public decimal Deductions { get; set; }
    public decimal Overtime { get; set; }
    public decimal Adjustments { get; set; }
    public decimal NetAmount { get; set; }
    /// <summary>"Unpaid" or "Paid".</summary>
    public string Status { get; set; }
    public bool IsSimulated { get; set; }
    public DateTime? PaidAt { get; set; }
    public string PaidByUserId { get; set; }
    /// <summary>The lines shown to the employee (JSON array).</summary>
    public string LinesJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public partial class Employee
{
    /// <summary>Staff only: "FullTime" (monthly salary, weekly pattern) or "Backup" (paid by the hour, fills gaps).</summary>
    public string EmploymentType { get; set; } = "FullTime";
}

/// <summary>An amount added to or taken from a later payslip to correct an earlier one.</summary>
public class PayAdjustment
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public DateOnly Month { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; }
    public string CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
}
