using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class PricingRuleHistory
{
    public long Id { get; set; }

    public int PricingRuleId { get; set; }

    public string ChangeType { get; set; }

    public string OldValuesJson { get; set; }

    public string NewValuesJson { get; set; }

    public string ChangedBy { get; set; }

    public DateTime ChangedAt { get; set; }

    public string Reason { get; set; }
}
