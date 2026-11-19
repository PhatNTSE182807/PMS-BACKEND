namespace PMS.Application.Models.DutySchedule;

/// <summary>One line of a payslip: the base salary, a deduction, extra hours or an adjustment.</summary>
public class PayLineModel
{
    /// <summary>"Base", "Absence", "UnpaidLeave", "ExtraHours", "Hours", "Adjustment" or "Simulated".</summary>
    public string Kind { get; set; }
    public string Label { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitAmount { get; set; }
    public decimal Amount { get; set; }
    /// <summary>Dates or shifts the line comes from, e.g. "2026-11-05" or "2026-11-20 12:00–18:00".</summary>
    public List<string> Details { get; set; } = [];
}

public class PaySlipModel
{
    public long Id { get; init; }
    public int EmployeeId { get; init; }
    public string EmployeeName { get; init; }
    public string EmployeeCode { get; init; }
    public int? ParkingLotId { get; init; }
    public string LotName { get; init; }
    public DateOnly Month { get; init; }
    public string Kind { get; init; }
    public decimal BaseAmount { get; init; }
    public decimal Deductions { get; init; }
    public decimal Overtime { get; init; }
    public decimal Adjustments { get; init; }
    public decimal NetAmount { get; init; }
    /// <summary>"Unpaid" or "Paid"; "Estimate" for a month that has not ended.</summary>
    public string Status { get; init; }
    public bool IsSimulated { get; init; }
    public DateTime? PaidAt { get; init; }
    public IReadOnlyList<PayLineModel> Lines { get; init; } = [];
}

public class PayrollReportModel
{
    public DateOnly Month { get; init; }
    /// <summary>The month is over, so it can be paid.</summary>
    public bool MonthEnded { get; init; }
    public int UnpaidCount { get; init; }
    public decimal UnpaidTotal { get; init; }
    public decimal PaidTotal { get; init; }
    public decimal SimulatedTotal { get; init; }
    public IReadOnlyList<PaySlipModel> Slips { get; init; } = [];
}

public class PayAllInput
{
    /// <summary>Any date in the month; the first day is used.</summary>
    public DateOnly Month { get; set; }
}

public record PayAllResultModel(DateOnly Month, int PaidCount, decimal Total);

public class SimulatePayInput
{
    public bool IncludeBase { get; set; } = true;
    /// <summary>Whole hours, 0–200.</summary>
    public int OvertimeHours { get; set; }
}

public class PayAdjustmentInput
{
    public int EmployeeId { get; set; }
    public DateOnly Month { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; }
}

public record PayrollEmployeeModel(int EmployeeId, string FullName, string EmployeeCode, string Kind, string LotName);
