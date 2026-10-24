using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Pricing;
using PMS.Application.Services;

namespace PMS.API.Controllers;

/// <summary>
/// Manager pricing and tariff configuration endpoints (SRS section 5, BR-09, BR-22).
/// All routes are scoped to a specific parking lot managed by the authenticated Manager (or Admin).
/// </summary>
[Route("api/manager/lots/{lotId:int}/pricing-rules")]
[Authorize(Roles = "Manager,Admin")]
public class ManagerPricingController(IPricingRuleService pricingRuleService) : ApiController
{
    /// <summary>Retrieves all pricing rules for a lot, optionally filtered by status.</summary>
    [HttpGet]
    public async Task<IActionResult> GetPricingRulesAsync(int lotId, [FromQuery] string status = null) =>
        Ok(ApiResult<IReadOnlyList<PricingRuleModel>>.Success(await pricingRuleService.GetPricingRulesAsync(lotId, status)));

    /// <summary>Gets a specific pricing rule by ID for a lot.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetPricingRuleByIdAsync(int lotId, int id) =>
        Ok(ApiResult<PricingRuleModel>.Success(await pricingRuleService.GetPricingRuleByIdAsync(lotId, id)));

    /// <summary>Creates a new pricing rule for a lot.</summary>
    [HttpPost]
    public async Task<IActionResult> CreatePricingRuleAsync(int lotId, [FromBody] CreatePricingRuleModel model) =>
        Ok(ApiResult<PricingRuleModel>.Success(await pricingRuleService.CreatePricingRuleAsync(lotId, model)));

    /// <summary>Updates an existing pricing rule.</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdatePricingRuleAsync(int lotId, int id, [FromBody] UpdatePricingRuleModel model) =>
        Ok(ApiResult<PricingRuleModel>.Success(await pricingRuleService.UpdatePricingRuleAsync(lotId, id, model)));

    /// <summary>Updates the status of a pricing rule (e.g. Draft -> Active, Active -> Inactive, or Archived).</summary>
    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> SetPricingRuleStatusAsync(int lotId, int id, [FromBody] SetPricingRuleStatusModel model) =>
        Ok(ApiResult<PricingRuleModel>.Success(await pricingRuleService.SetPricingRuleStatusAsync(lotId, id, model)));

    /// <summary>Soft deletes / archives a pricing rule.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeletePricingRuleAsync(int lotId, int id)
    {
        await pricingRuleService.DeletePricingRuleAsync(lotId, id);
        return Ok(ApiResult<bool>.Success(true));
    }

    /// <summary>Simulates checkout price calculation under active tariffs (sandbox).</summary>
    [HttpPost("simulate")]
    public async Task<IActionResult> SimulatePricingAsync(int lotId, [FromBody] PricingSimulationRequestModel model) =>
        Ok(ApiResult<PricingSimulationResultModel>.Success(await pricingRuleService.SimulatePricingAsync(lotId, model)));
}
