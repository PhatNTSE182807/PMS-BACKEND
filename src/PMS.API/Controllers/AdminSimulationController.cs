using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Operations;
using PMS.Application.Services;
using PMS.Shared.Services;

namespace PMS.API.Controllers;

/// <summary>
/// Admin "Parking Simulation" screen: runs fees and booking times in seconds for demos, in every lot.
/// </summary>
[Route("api/admin/simulation")]
[Authorize(Roles = "Admin")]
public class AdminSimulationController(IParkingSimulationService simulation, IClaimService claimService) : ApiController
{
    [HttpGet]
    public async Task<IActionResult> GetAsync() =>
        Ok(ApiResult<ParkingSimulationResponseModel>.Success(await simulation.GetAsync()));

    [HttpPut]
    public async Task<IActionResult> UpdateAsync([FromBody] UpdateParkingSimulationModel model) =>
        Ok(ApiResult<ParkingSimulationResponseModel>.Success(await simulation.UpdateAsync(model, claimService.GetUserId())));
}
