using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Pricing;
using PMS.Application.Services;

namespace PMS.API.Controllers;

/// <summary>
/// Manager pricing and tariff configuration endpoints (SRS section 5, BR-09, BR-22).
/// All routes are scoped to a specific parking lot managed by the authenticated Manager (or Admin).
/// Errors come back as <c>{ succeeded: false, errors: [...] }</c> with the matching HTTP status
/// (mapped by the exception middleware): 400 invalid request, 403 lot not assigned to the caller,
/// 404 unknown lot or rule, 409 the lot was busy.
/// </summary>
[Route("api/manager/lots/{lotId:int}/pricing-rules")]
[Authorize(Roles = "Manager,Admin")]
[ProducesResponseType(typeof(ApiResult<string>), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiResult<string>), StatusCodes.Status403Forbidden)]
public class ManagerPricingController(IPricingRuleService pricingRuleService) : ApiController
{
    /// <summary>Retrieves all pricing rules for a lot, optionally filtered by status.</summary>
    /// <param name="lotId">The parking lot.</param>
    /// <param name="status">Optional rule status filter (Draft, Active, Inactive, Archived).</param>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResult<IReadOnlyList<PricingRuleModel>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult<string>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPricingRulesAsync(int lotId, [FromQuery] string status = null) =>
        Ok(ApiResult<IReadOnlyList<PricingRuleModel>>.Success(await pricingRuleService.GetPricingRulesAsync(lotId, status)));

    /// <summary>Gets a specific pricing rule by ID for a lot.</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ApiResult<PricingRuleModel>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult<string>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPricingRuleByIdAsync(int lotId, int id) =>
        Ok(ApiResult<PricingRuleModel>.Success(await pricingRuleService.GetPricingRuleByIdAsync(lotId, id)));

    /// <summary>Creates a new pricing rule for a lot.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResult<PricingRuleModel>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult<string>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult<string>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreatePricingRuleAsync(int lotId, [FromBody] CreatePricingRuleModel model) =>
        Ok(ApiResult<PricingRuleModel>.Success(await pricingRuleService.CreatePricingRuleAsync(lotId, model)));

    /// <summary>
    /// Updates a pricing rule. A new price is live at once and, for an Active rule, every customer is told.
    /// </summary>
    /// <remarks>
    /// The response is the rule after the update. <c>priceUpdateStatus</c> tells what happened to the charging
    /// fields (charge model, entry fee, blocks, day/night prices, daily cap and the fees):
    ///
    /// * <c>Applied</c> - the new price is live. Vehicles already inside keep the price they entered with; every
    ///   vehicle entering from now on, booked or walk-in, is charged the new one.
    /// * <c>Unchanged</c> - the request equals the live price.
    ///
    /// Other fields (name, zone, vehicle type, priority, validity, status) are saved in both cases.
    /// A rule shared by the whole system (no lot) cannot be changed here.
    /// </remarks>
    /// <param name="lotId">The parking lot the rule belongs to.</param>
    /// <param name="id">The pricing rule.</param>
    /// <param name="model">The full new values of the rule.</param>
    /// <response code="200">Updated; see <c>priceUpdateStatus</c>.</response>
    /// <response code="400">Invalid values, or the rule is shared by the whole system.</response>
    /// <response code="404">The lot or the rule does not exist.</response>
    /// <response code="409">The lot kept changing while saving (vehicles entering or leaving); retry the request.</response>
    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(ApiResult<PricingRuleModel>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult<string>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult<string>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult<string>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdatePricingRuleAsync(int lotId, int id, [FromBody] UpdatePricingRuleModel model) =>
        Ok(ApiResult<PricingRuleModel>.Success(await pricingRuleService.UpdatePricingRuleAsync(lotId, id, model)));

    /// <summary>Updates the status of a pricing rule (e.g. Draft -> Active, Active -> Inactive, or Archived).</summary>
    [HttpPatch("{id:int}/status")]
    [ProducesResponseType(typeof(ApiResult<PricingRuleModel>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult<string>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult<string>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetPricingRuleStatusAsync(int lotId, int id, [FromBody] SetPricingRuleStatusModel model) =>
        Ok(ApiResult<PricingRuleModel>.Success(await pricingRuleService.SetPricingRuleStatusAsync(lotId, id, model)));

    /// <summary>Soft deletes / archives a pricing rule.</summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(typeof(ApiResult<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult<string>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeletePricingRuleAsync(int lotId, int id)
    {
        await pricingRuleService.DeletePricingRuleAsync(lotId, id);
        return Ok(ApiResult<bool>.Success(true));
    }

    /// <summary>Simulates checkout price calculation under active tariffs (sandbox).</summary>
    [HttpPost("simulate")]
    [ProducesResponseType(typeof(ApiResult<PricingSimulationResultModel>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult<string>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult<string>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SimulatePricingAsync(int lotId, [FromBody] PricingSimulationRequestModel model) =>
        Ok(ApiResult<PricingSimulationResultModel>.Success(await pricingRuleService.SimulatePricingAsync(lotId, model)));
}
