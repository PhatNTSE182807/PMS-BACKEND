using System;

namespace PMS.Application.Models.Pricing;

/// <summary>The values of <see cref="PMS.DataAccess.Persistence.Entities.PricingRuleDraft.Status"/>.</summary>
public static class PricingRuleDraftStatuses
{
    /// <summary>Saved, not applied yet: the gates still charge the live prices.</summary>
    public const string Open = "Open";

    /// <summary>Applied to the rule: its values became the live prices.</summary>
    public const string Applied = "Applied";

    /// <summary>Dropped by a Manager; kept for the record.</summary>
    public const string Discarded = "Discarded";
}

/// <summary>A price change saved as a draft for one pricing rule.</summary>
public class PricingRuleDraftModel
{
    public int Id { get; set; }

    public int PricingRuleId { get; set; }

    /// <summary>The proposed values, in the same shape as a rule update.</summary>
    public UpdatePricingRuleModel Values { get; set; }

    public string Note { get; set; }

    /// <summary>The rule version the draft was based on.</summary>
    public int BaseVersionNo { get; set; }

    /// <summary>The rule's live version now; when it is newer than <see cref="BaseVersionNo"/> the prices changed since.</summary>
    public int CurrentVersionNo { get; set; }

    /// <summary>"Open", "Applied" or "Discarded".</summary>
    public string Status { get; set; }

    public string CreatedBy { get; set; }

    /// <summary>The full name of the Manager who saved the draft, when known.</summary>
    public string CreatedByName { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}

/// <summary>Request to save (create or update) a price change as a draft.</summary>
public class SavePricingRuleDraftModel
{
    /// <summary>The proposed values, in the same shape as a rule update.</summary>
    public UpdatePricingRuleModel Values { get; set; }

    /// <summary>Optional note, up to 500 characters.</summary>
    public string Note { get; set; }
}
