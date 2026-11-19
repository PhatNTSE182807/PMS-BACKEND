using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PMS.Application.Models;
using PMS.Application.Models.Layout;
using PMS.Application.Models.ParkingLot;
using PMS.Application.Models.Pricing;
using PMS.Application.Services;
using PMS.Shared.Services;

namespace PMS.API.Controllers;

[Route("api/lots")]
public class LotsController(
    IParkingLotService parkingLotService,
    IPricingRuleService pricingRuleService,
    IClaimService claimService) : ApiController
{
    /// <summary>Lists parking lots, filtered by a search term and status; deleted lots only on request.</summary>
    [HttpGet]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> GetAllAsync(
        [FromQuery] string searchTerm = null,
        [FromQuery] string status = null,
        [FromQuery] bool includeDeleted = false)
    {
        return Ok(ApiResult<IEnumerable<ParkingLotResponseModel>>.Success(
            await parkingLotService.GetAllAsync(searchTerm, status, includeDeleted)));
    }

    /// <summary>
    /// Real-time occupancy and performance of every active lot for the Admin Lot Performance dashboard.
    /// </summary>
    [HttpGet("/api/v1/parking/lots/capacity")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> GetCapacityOverviewAsync(System.Threading.CancellationToken cancellationToken)
    {
        return Ok(ApiResult<IReadOnlyList<ParkingLotCapacityModel>>.Success(
            await parkingLotService.GetCapacityOverviewAsync(cancellationToken)));
    }

    /// <summary>Open lots for guests and customers (name, address, location and free spaces), no sign-in needed.</summary>
    [HttpGet("/api/public/lots")]
    [AllowAnonymous]
    [EnableRateLimiting(ApiDependencyInjection.PublicLayoutRateLimit)]
    public async Task<IActionResult> GetPublicLotsAsync([FromQuery] string searchTerm = null)
    {
        return Ok(ApiResult<IEnumerable<PublicParkingLotResponseModel>>.Success(
            await parkingLotService.GetPublicLotsAsync(searchTerm)));
    }

    /// <summary>
    /// Public prices of a lot, one per vehicle type: what the gates charge now (the lot's Active pricing rule,
    /// or the system default). Rate limited like the other public lot data.
    /// </summary>
    [HttpGet("/api/public/lots/{id:int}/prices")]
    [AllowAnonymous]
    [EnableRateLimiting(ApiDependencyInjection.PublicLayoutRateLimit)]
    public async Task<IActionResult> GetPublicPricesAsync(int id)
    {
        return Ok(ApiResult<IReadOnlyList<PublicLotPriceModel>>.Success(await pricingRuleService.GetPublicPricesAsync(id)));
    }

    /// <summary>One parking lot with its configuration.</summary>
    [HttpGet("{id:int}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> GetByIdAsync(int id)
    {
        return Ok(ApiResult<ParkingLotResponseModel>.Success(
            await parkingLotService.GetByIdAsync(id)));
    }

    /// <summary>Registers a new parking lot.</summary>
    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateAsync([FromBody] CreateParkingLotModel model)
    {
        var createdBy = claimService.GetUserId();
        return Ok(ApiResult<ParkingLotResponseModel>.Success(
            await parkingLotService.CreateAsync(model, createdBy)));
    }

    /// <summary>Only Admins configure a lot; Managers operate it (status below).</summary>
    [HttpPut("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateAsync(int id, [FromBody] UpdateParkingLotModel model)
    {
        return Ok(ApiResult<ParkingLotResponseModel>.Success(
            await parkingLotService.UpdateAsync(id, model)));
    }

    /// <summary>Builds the zones, slots and 3D layout from a ready-made model, for a lot that has no zones yet.</summary>
    [HttpPut("{id:int}/template")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ApplyTemplateAsync(int id, [FromBody] ApplyLotTemplateModel model)
    {
        return Ok(ApiResult<ParkingLotResponseModel>.Success(
            await parkingLotService.ApplyTemplateAsync(id, model.LayoutTemplate, claimService.GetUserId())));
    }

    /// <summary>Operational: open, temporarily close or put the lot into maintenance.</summary>
    [HttpPatch("{id:int}/status")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> UpdateStatusAsync(int id, [FromBody] UpdateParkingLotStatusModel model)
    {
        return Ok(ApiResult<bool>.Success(
            await parkingLotService.UpdateStatusAsync(id, model)));
    }

    /// <summary>Soft-deletes a parking lot; it stays in the history but can no longer be used.</summary>
    [HttpDelete("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteAsync(int id)
    {
        return Ok(ApiResult<bool>.Success(
            await parkingLotService.DeleteAsync(id)));
    }
}
