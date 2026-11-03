using System;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// A price change the Manager asked for while vehicles were still inside the lot. At most one per pricing
/// rule: asking again before it is applied overwrites it. It becomes the rule's live price when the lot is
/// empty, and the row is then removed.
/// </summary>
public partial class PendingPriceChange
{
    public long Id { get; set; }

    public int ParkingLotId { get; set; }

    public int PricingRuleId { get; set; }

    /// <summary>The new price fields of the rule (entry fee, block prices, day / night prices, daily cap), applied together.</summary>
    public string ProposedValuesJson { get; set; }

    /// <summary>1 for the first request; +1 each time it is overwritten before being applied.</summary>
    public int RevisionNo { get; set; } = 1;

    public string RequestedBy { get; set; }

    public DateTime RequestedAt { get; set; }

    /// <summary>When the request was last overwritten; null while it is the first one.</summary>
    public DateTime? UpdatedAt { get; set; }

    public virtual ParkingLot ParkingLot { get; set; }

    public virtual PricingRule PricingRule { get; set; }
}
