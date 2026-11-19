namespace PMS.Application.Models.DutySchedule;

/// <summary>Rules of the duty board, attendance and leave ("WorkSchedule" section of appsettings).</summary>
public class DutyOptions
{
    /// <summary>Staff can only use the POS (vehicles, cash) while checked in to a shift.</summary>
    public bool RequireShiftForPos { get; set; } = true;
    /// <summary>A shift can be started this many minutes before it begins.</summary>
    public int EarlyStartMinutes { get; set; } = 30;
    /// <summary>No check-in this many minutes after the start: the Staff is absent.</summary>
    public int AbsentAfterMinutes { get; set; } = 30;
    /// <summary>A shift not ended this many minutes after its end is ended at the scheduled time.</summary>
    public int EndGraceMinutes { get; set; } = 15;
    /// <summary>Calendar days of leave per person and month, over all their requests.</summary>
    public int MaxLeaveDaysPerMonth { get; set; } = 7;
    /// <summary>Paid leave days per year when an Admin has not set another number.</summary>
    public int AnnualPaidLeaveDays { get; set; } = 12;
}

/// <summary>Fixed pay rules ("Payroll" section of appsettings), in VND.</summary>
public class PayrollOptions
{
    public decimal FullTimeStaffMonthly { get; set; } = 5_800_000m;
    public decimal ManagerMonthly { get; set; } = 10_000_000m;
    /// <summary>Backup Staff hours and extra shifts of full-time Staff.</summary>
    public decimal HourlyRate { get; set; } = 30_000m;
    /// <summary>A day of absence or unpaid leave costs monthly salary / this number.</summary>
    public int StandardDays { get; set; } = 26;
    /// <summary>Staff and Managers may create a simulated payslip for themselves (demo).</summary>
    public bool AllowSimulation { get; set; } = true;
}

public static class DutySlots
{
    public const int Count = 4;
    public const int Hours = 6;
    public static readonly string[] Names = ["Night", "Morning", "Afternoon", "Evening"];
    public static TimeOnly Start(int slot) => new(slot * Hours, 0);
    public static string Label(int slot) => $"{slot * Hours:00}:00–{(slot + 1) * Hours:00}:00";
}

public static class EmploymentTypes
{
    public const string FullTime = "FullTime";
    public const string Backup = "Backup";
    public const string Manager = "Manager";
    public static bool IsValid(string value) => value is FullTime or Backup;
}

public record DutyStaffModel(int EmployeeId, string UserId, string FullName, string EmployeeCode, string EmploymentType, bool IsActive);

/// <summary>One shift of one date on the board.</summary>
public class DutyCellModel
{
    public DateOnly Date { get; init; }
    public int Slot { get; init; }
    public DateTime StartsAt { get; init; }
    public DateTime EndsAt { get; init; }
    public int? EmployeeId { get; init; }
    public string EmployeeName { get; init; }
    public string EmploymentType { get; init; }
    /// <summary>The shift was changed for this date only (it differs from the weekly pattern).</summary>
    public bool IsOneDayChange { get; init; }
    /// <summary>Who is on approved leave for this shift (the person replaced).</summary>
    public string OnLeaveName { get; init; }
    /// <summary>The person on the shift has asked for leave on this date (Manager and the person only).</summary>
    public bool LeavePending { get; init; }
    /// <summary>"Working", "Completed", "Absent" or null (only the person themselves and the Manager).</summary>
    public string AttendanceStatus { get; init; }
    public bool IsAutoEnded { get; init; }
    public DateTime? CheckInAt { get; init; }
    public DateTime? CheckOutAt { get; init; }
    public bool IsPast { get; init; }
    public bool IsMine { get; init; }
}

public class DutyWeekModel
{
    public int ParkingLotId { get; init; }
    public string LotName { get; init; }
    public string TimeZoneId { get; init; }
    public DateOnly WeekStart { get; init; }
    public DateOnly Today { get; init; }
    public IReadOnlyList<DutyCellModel> Cells { get; init; } = [];
    /// <summary>Manager only: Staff of the lot to choose from.</summary>
    public IReadOnlyList<DutyStaffModel> Staff { get; init; } = [];
    public int EmptyShifts { get; init; }
    public int PendingLeaveRequests { get; init; }
}

public class DutyCellInput
{
    public DateOnly Date { get; set; }
    public int Slot { get; set; }
    /// <summary>Null leaves the shift empty.</summary>
    public int? EmployeeId { get; set; }
    /// <summary>"Day" (this date only) or "Following" (this and following weeks).</summary>
    public string Scope { get; set; } = "Day";
    public string Reason { get; set; }
}

public record DutyCandidateModel(int EmployeeId, string FullName, string EmployeeCode, string EmploymentType,
    bool Eligible, string Warning, string BlockedReason, int ShiftsThisWeek);

public class DutyShiftModel
{
    public int ParkingLotId { get; init; }
    public string LotName { get; init; }
    public DateOnly Date { get; init; }
    public int Slot { get; init; }
    public DateTime StartsAt { get; init; }
    public DateTime EndsAt { get; init; }
    public string AttendanceStatus { get; init; }
    public bool IsAutoEnded { get; init; }
    public DateTime? CheckInAt { get; init; }
    public DateTime? CheckOutAt { get; init; }
}

/// <summary>What the signed-in Staff member can do now.</summary>
public class DutyStatusModel
{
    public bool RequireShiftForPos { get; init; }
    /// <summary>Checked in to a shift that has not ended: the POS is open.</summary>
    public bool OnShift { get; init; }
    public bool CanStart { get; init; }
    public bool CanEnd { get; init; }
    /// <summary>The shift being worked, or the one whose start window is open.</summary>
    public DutyShiftModel Current { get; init; }
    public DutyShiftModel Next { get; init; }
    public string EmploymentType { get; init; }
}

public class DutyAttendanceInput
{
    public DateOnly Date { get; set; }
    public int Slot { get; set; }
    public int EmployeeId { get; set; }
    /// <summary>Both empty: absent.</summary>
    public DateTime? CheckInAt { get; set; }
    public DateTime? CheckOutAt { get; set; }
    public string Reason { get; set; }
}

public class LeaveRequestInput
{
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    public string Reason { get; set; }
}

public record LeaveShiftModel(DateOnly Date, int Slot, DateTime StartsAt, DateTime EndsAt);

public class LeavePreviewModel
{
    public DateOnly FromDate { get; init; }
    public DateOnly ToDate { get; init; }
    public int CalendarDays { get; init; }
    public IReadOnlyList<LeaveShiftModel> Shifts { get; init; } = [];
    public int PaidDays { get; init; }
    public int UnpaidDays { get; init; }
    /// <summary>Why the request cannot be sent; null when it can.</summary>
    public string Problem { get; init; }
}

public class LeaveBalanceModel
{
    public int Year { get; init; }
    public int EntitledDays { get; init; }
    public int PaidDaysUsed { get; init; }
    public int RemainingPaidDays => Math.Max(0, EntitledDays - PaidDaysUsed);
    public int MaxDaysPerMonth { get; init; }
    public int DaysThisMonth { get; init; }
}

public class LeaveRequestModel
{
    public int Id { get; init; }
    public int EmployeeId { get; init; }
    public string EmployeeName { get; init; }
    public string EmploymentType { get; init; }
    public DateOnly FromDate { get; init; }
    public DateOnly ToDate { get; init; }
    public string Reason { get; init; }
    public string Status { get; init; }
    public string DecisionReason { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? DecidedAt { get; init; }
    public int PaidDays { get; init; }
    public int UnpaidDays { get; init; }
    /// <summary>Pending: the shifts that need someone else.</summary>
    public IReadOnlyList<LeaveShiftModel> Shifts { get; init; } = [];
}

public class MyLeaveModel
{
    public LeaveBalanceModel Balance { get; init; }
    public IReadOnlyList<LeaveRequestModel> Requests { get; init; } = [];
    /// <summary>First month after joining mid-month: no leave.</summary>
    public DateOnly? NoLeaveUntil { get; init; }
}

public class LeaveReplacementInput
{
    public DateOnly Date { get; set; }
    public int Slot { get; set; }
    public int? EmployeeId { get; set; }
}

public class LeaveDecisionInput
{
    public string Reason { get; set; }
    public List<LeaveReplacementInput> Replacements { get; set; } = [];
}

public record LeaveBalanceRowModel(int EmployeeId, string UserId, string FullName, string EmployeeCode, string LotName,
    string EmploymentType, int Year, int EntitledDays, int PaidDaysUsed, int Revision);

public class LeaveBalanceInput
{
    public int EntitledDays { get; set; }
    public int Revision { get; set; }
}
