using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Zone;
using PMS.Application.Services;

namespace PMS.API.Controllers;

[Route("api/zones")]
[Authorize(Roles = "Admin,Manager")]
public class ZonesController(IZoneService zoneService) : ApiController
{
    [HttpGet]
    public async Task<IActionResult> GetAllAsync([FromQuery] int? lotId = null)
    {
        return Ok(ApiResult<IEnumerable<ZoneResponseModel>>.Success(
            await zoneService.GetAllAsync(lotId)));
    }

    [HttpGet("/api/lots/{lotId:int}/zones")]
    [HttpGet("/api/parking-lots/{lotId:int}/zones")]
    public async Task<IActionResult> GetByLotIdAsync(int lotId)
    {
        return Ok(ApiResult<IEnumerable<ZoneResponseModel>>.Success(
            await zoneService.GetByLotIdAsync(lotId)));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetByIdAsync(int id)
    {
        return Ok(ApiResult<ZoneResponseModel>.Success(
            await zoneService.GetByIdAsync(id)));
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] CreateZoneModel model)
    {
        return Ok(ApiResult<ZoneResponseModel>.Success(
            await zoneService.CreateAsync(model)));
    }

    [HttpPost("/api/lots/{lotId:int}/zones")]
    [HttpPost("/api/parking-lots/{lotId:int}/zones")]
    public async Task<IActionResult> CreateForLotAsync(int lotId, [FromBody] CreateZoneModel model)
    {
        model.ParkingLotId = lotId;
        return Ok(ApiResult<ZoneResponseModel>.Success(
            await zoneService.CreateAsync(model)));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateAsync(int id, [FromBody] UpdateZoneModel model)
    {
        return Ok(ApiResult<ZoneResponseModel>.Success(
            await zoneService.UpdateAsync(id, model)));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteAsync(int id)
    {
        return Ok(ApiResult<bool>.Success(
            await zoneService.DeleteAsync(id)));
    }
}
