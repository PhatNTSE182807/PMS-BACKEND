using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class EmployeePerformanceReview
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }

    public DateOnly PeriodFrom { get; set; }

    public DateOnly PeriodTo { get; set; }

    public int SessionsHandled { get; set; }

    public decimal CashDiscrepancy { get; set; }

    public decimal? Score { get; set; }

    public string Comment { get; set; }

    public string ReviewedBy { get; set; }

    public DateTime CreatedAt { get; set; }
}
