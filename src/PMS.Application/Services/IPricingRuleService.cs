using System.Collections.Generic;
using System.Threading.Tasks;
using PMS.Application.Models.Pricing;

namespace PMS.Application.Services;

/// <summary>
/// Service interface for managing pricing rules and tariffs for parking lots (SRS section 5, BR-09, BR-22).
/// Scope is restricted to the lots the Manager manages (or all lots for Admin).
/// </summary>
public interface IPricingRuleService
{
    /// <summary>Retrieves all pricing rules defined for a lot, optionally filtered by status.</summary>
    Task<IReadOnlyList<PricingRuleModel>> GetPricingRulesAsync(int lotId, string status = null);

    /// <summary>Gets a specific pricing rule by ID for a lot.</summary>
    Task<PricingRuleModel> GetPricingRuleByIdAsync(int lotId, int ruleId);

    /// <summary>Creates a new pricing rule for a lot.</summary>
    Task<PricingRuleModel> CreatePricingRuleAsync(int lotId, CreatePricingRuleModel model);

    /// <summary>Updates an existing pricing rule.</summary>
    Task<PricingRuleModel> UpdatePricingRuleAsync(int lotId, int ruleId, UpdatePricingRuleModel model);

    /// <summary>Updates the status of a pricing rule (e.g. Draft -> Active, Active -> Inactive, or Archived).</summary>
    Task<PricingRuleModel> SetPricingRuleStatusAsync(int lotId, int ruleId, SetPricingRuleStatusModel model);

    /// <summary>Soft deletes / archives a pricing rule.</summary>
    Task DeletePricingRuleAsync(int lotId, int ruleId);

    /// <summary>Simulates checkout price calculation under active tariffs without mutating data.</summary>
    Task<PricingSimulationResultModel> SimulatePricingAsync(int lotId, PricingSimulationRequestModel model);
}
