using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.ParkingLot;
using PMS.Application.Services;
using PMS.Shared.Services;

namespace PMS.API.Controllers;

[Route("api/lots")]
[Route("api/parking-lots")]
public class LotsController(
    IParkingLotService parkingLotService,
    IClaimService claimService) : ApiController
{
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

    [HttpGet("/api/public/lots")]
    [HttpGet("/api/public/parking-lots")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPublicLotsAsync([FromQuery] string searchTerm = null)
    {
        return Ok(ApiResult<IEnumerable<PublicParkingLotResponseModel>>.Success(
            await parkingLotService.GetPublicLotsAsync(searchTerm)));
    }

    [HttpGet("{id:int}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> GetByIdAsync(int id)
    {
        return Ok(ApiResult<ParkingLotResponseModel>.Success(
            await parkingLotService.GetByIdAsync(id)));
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateAsync([FromBody] CreateParkingLotModel model)
    {
        var createdBy = claimService.GetUserId();
        return Ok(ApiResult<ParkingLotResponseModel>.Success(
            await parkingLotService.CreateAsync(model, createdBy)));
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> UpdateAsync(int id, [FromBody] UpdateParkingLotModel model)
    {
        return Ok(ApiResult<ParkingLotResponseModel>.Success(
            await parkingLotService.UpdateAsync(id, model)));
    }

    [HttpPatch("{id:int}/status")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> UpdateStatusAsync(int id, [FromBody] UpdateParkingLotStatusModel model)
    {
        return Ok(ApiResult<bool>.Success(
            await parkingLotService.UpdateStatusAsync(id, model)));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteAsync(int id)
    {
        return Ok(ApiResult<bool>.Success(
            await parkingLotService.DeleteAsync(id)));
    }
}
