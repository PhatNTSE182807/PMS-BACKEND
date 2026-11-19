using System.Collections.Generic;
using System.Threading.Tasks;
using PMS.Application.Models.Pricing;

namespace PMS.Application.Services;

/// <summary>
/// Price changes a Manager saves as drafts before applying them. A draft charges nobody; applying it is the
/// same as saving the prices on the rule (new version, history, customers told for an Active rule).
/// Scope is the lots the Manager manages (or all lots for Admin).
/// </summary>
public interface IPricingRuleDraftService
{
    /// <summary>The open drafts of every rule of the lot that is not archived, newest first.</summary>
    Task<IReadOnlyList<PricingRuleDraftModel>> GetDraftsAsync(int lotId);

    /// <summary>Saves a new draft for a rule.</summary>
    Task<PricingRuleDraftModel> CreateDraftAsync(int lotId, int ruleId, SavePricingRuleDraftModel model);

    /// <summary>Replaces the values and note of an open draft.</summary>
    Task<PricingRuleDraftModel> UpdateDraftAsync(int lotId, int ruleId, int draftId, SavePricingRuleDraftModel model);

    /// <summary>Applies an open draft to its rule and closes it; returns the rule after the update.</summary>
    Task<PricingRuleModel> ApplyDraftAsync(int lotId, int ruleId, int draftId);

    /// <summary>Closes an open draft without applying it (the row is kept).</summary>
    Task DiscardDraftAsync(int lotId, int ruleId, int draftId);
}
