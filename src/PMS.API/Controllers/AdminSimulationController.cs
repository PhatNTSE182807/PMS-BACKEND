using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Operations;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.Shared.Services;

namespace PMS.API.Controllers;

/// <summary>
/// Admin "Parking Simulation" screen: runs fees and booking times in seconds for demos, in every lot.
/// </summary>
[Route("api/admin/simulation")]
[Authorize(Roles = "Admin")]
public class AdminSimulationController(
    IParkingSimulationService simulation,
    WalkInSimulationService walkIns,
    IClaimService claimService) : ApiController
{
    /// <summary>Simulated walk-ins still inside (all lots, or one), with how many left and what they paid.</summary>
    [HttpGet("walk-ins")]
    public async Task<IActionResult> GetWalkInsAsync([FromQuery] int? lotId = null) =>
        Ok(ApiResult<WalkInSimulationOverviewModel>.Success(await walkIns.GetOverviewAsync(lotId)));

    /// <summary>Sends a batch of walk-ins with random plates through the lot's entry lane; each leaves after its stay.</summary>
    [HttpPost("walk-ins")]
    public async Task<IActionResult> DeployWalkInsAsync([FromBody] DeployWalkInsModel model) =>
        Ok(ApiResult<DeployWalkInsResultModel>.Success(await walkIns.DeployAsync(model)));

    /// <summary>Every simulated vehicle (of one lot, or of all) pays and leaves now.</summary>
    [HttpPost("walk-ins/check-out-all")]
    public async Task<IActionResult> CheckOutAllWalkInsAsync([FromQuery] int? lotId = null) =>
        Ok(ApiResult<WalkInCheckOutResultModel>.Success(await walkIns.CheckOutAllAsync(lotId)));

    /// <summary>A simulated walk-in parks in the bay of a confirmed booking (the next one, the one given, or the one in this bay).</summary>
    [HttpPost("walk-ins/occupy-booked-bay")]
    public async Task<IActionResult> OccupyBookedBayAsync([FromQuery] int lotId, [FromQuery] long? reservationId = null, [FromQuery] int? slotId = null) =>
        Ok(ApiResult<BookedBayTakenModel>.Success(await walkIns.OccupyBookedBayAsync(lotId, reservationId, slotId)));

    /// <summary>One simulated walk-in for a free bay picked on the 3D model: it waits at the entry lane for the Staff.</summary>
    [HttpPost("walk-ins/bay")]
    public async Task<IActionResult> DeployWalkInToBayAsync([FromBody] DeployWalkInToBayModel model) =>
        Ok(ApiResult<GateRequestModel>.Success(await walkIns.DeployToBayAsync(model)));

    /// <summary>Current simulation settings (seconds used instead of real minutes) and the normal values they replace.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAsync() =>
        Ok(ApiResult<ParkingSimulationResponseModel>.Success(await simulation.GetAsync()));

    /// <summary>Turns the simulation on or off and sets the shortened times for fees and booking deadlines.</summary>
    [HttpPut]
    public async Task<IActionResult> UpdateAsync([FromBody] UpdateParkingSimulationModel model) =>
        Ok(ApiResult<ParkingSimulationResponseModel>.Success(await simulation.UpdateAsync(model, claimService.GetUserId())));
}
