using System.Collections.Generic;
using System.Threading.Tasks;
using PMS.Application.Models.Pricing;

namespace PMS.Application.Services;

/// <summary>Reads a lot's pricing history (dbo.PricingRuleHistories) for the lot's Manager or any Admin.</summary>
public interface IPricingHistoryService
{
    /// <summary>
    /// The newest history entries of the lot's rules (archived ones included), optionally for one rule only.
    /// </summary>
    /// <param name="lotId">The parking lot.</param>
    /// <param name="ruleId">Only this rule, when set.</param>
    /// <param name="take">How many entries, 1 to 500 (default 100).</param>
    Task<IReadOnlyList<PricingRuleHistoryModel>> GetHistoryAsync(int lotId, int? ruleId = null, int take = 100);
}
