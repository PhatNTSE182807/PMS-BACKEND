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
    /// <summary>Lists zones, all or of one lot.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAllAsync([FromQuery] int? lotId = null)
    {
        return Ok(ApiResult<IEnumerable<ZoneResponseModel>>.Success(
            await zoneService.GetAllAsync(lotId)));
    }

    /// <summary>Zones of one lot.</summary>
    [HttpGet("/api/lots/{lotId:int}/zones")]
    public async Task<IActionResult> GetByLotIdAsync(int lotId)
    {
        return Ok(ApiResult<IEnumerable<ZoneResponseModel>>.Success(
            await zoneService.GetByLotIdAsync(lotId)));
    }

    /// <summary>One zone.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetByIdAsync(int id)
    {
        return Ok(ApiResult<ZoneResponseModel>.Success(
            await zoneService.GetByIdAsync(id)));
    }

    // Zones are generated from the lot's ready-made model (LotTemplates); there is no create or delete.
    // Only an Admin adjusts a zone (name, reservation share); Managers do not configure lots.
    /// <summary>Updates a zone's configuration: name, zone type, floor and capacity.</summary>
    [HttpPut("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateAsync(int id, [FromBody] UpdateZoneModel model)
    {
        return Ok(ApiResult<ZoneResponseModel>.Success(
            await zoneService.UpdateAsync(id, model)));
    }
}
