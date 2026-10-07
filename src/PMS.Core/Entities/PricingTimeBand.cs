using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class PricingTimeBand
{
    public int Id { get; set; }

    public int PricingRuleId { get; set; }

    public string Name { get; set; }

    public string DayType { get; set; }

    public TimeOnly StartTime { get; set; }

    public TimeOnly EndTime { get; set; }

    public string AdjustmentType { get; set; }

    public decimal AdjustmentValue { get; set; }

    public bool IsPeak { get; set; }

    public bool IsActive { get; set; }
}
