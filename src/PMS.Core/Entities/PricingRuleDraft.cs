using System;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// A price change a Manager saved for later instead of applying it (team addition, 2026-11-17).
/// A draft charges nobody: the gates keep the rule's live prices until the draft is applied.
/// Drafts are never deleted: applying or discarding one only closes it.
/// </summary>
public class PricingRuleDraft
{
    public int Id { get; set; }

    public int PricingRuleId { get; set; }

    /// <summary>The lot the draft was saved in (the rule's lot), so a lot's drafts are read in one query.</summary>
    public int ParkingLotId { get; set; }

    /// <summary>The proposed rule values: an <c>UpdatePricingRuleModel</c> as JSON.</summary>
    public string ValuesJson { get; set; }

    /// <summary>Optional note of the Manager (why, or when to apply it).</summary>
    public string Note { get; set; }

    /// <summary>The rule version the draft was based on; a newer live version means the draft may be outdated.</summary>
    public int BaseVersionNo { get; set; }

    /// <summary>One of <c>PricingRuleDraftStatuses</c>: Open, Applied or Discarded.</summary>
    public string Status { get; set; }

    public string CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public string UpdatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    /// <summary>Who applied or discarded the draft.</summary>
    public string ClosedBy { get; set; }

    public DateTime? ClosedAt { get; set; }
}
