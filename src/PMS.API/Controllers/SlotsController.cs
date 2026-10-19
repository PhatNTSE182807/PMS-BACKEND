using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Slot;
using PMS.Application.Services;

namespace PMS.API.Controllers;

[Route("api/slots")]
[Authorize(Roles = "Admin,Manager")]
public class SlotsController(ISlotService slotService) : ApiController
{
    [HttpGet]
    public async Task<IActionResult> GetAllAsync(
        [FromQuery] int? zoneId = null,
        [FromQuery] int? vehicleTypeId = null,
        [FromQuery] string status = null,
        [FromQuery] bool? isActive = null)
    {
        return Ok(ApiResult<IEnumerable<SlotResponseModel>>.Success(
            await slotService.GetAllAsync(zoneId, vehicleTypeId, status, isActive)));
    }

    [HttpGet("/api/zones/{zoneId:int}/slots")]
    public async Task<IActionResult> GetByZoneIdAsync(int zoneId)
    {
        return Ok(ApiResult<IEnumerable<SlotResponseModel>>.Success(
            await slotService.GetByZoneIdAsync(zoneId)));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetByIdAsync(int id)
    {
        return Ok(ApiResult<SlotResponseModel>.Success(
            await slotService.GetByIdAsync(id)));
    }

    // Slots are generated from the lot's ready-made model (LotTemplates); there is no create or delete.
    [HttpPut("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateAsync(int id, [FromBody] UpdateSlotModel model)
    {
        return Ok(ApiResult<SlotResponseModel>.Success(
            await slotService.UpdateAsync(id, model)));
    }

    /// <summary>Operational: put a bay into or out of maintenance.</summary>
    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> UpdateStatusAsync(int id, [FromBody] UpdateSlotStatusModel model)
    {
        return Ok(ApiResult<bool>.Success(
            await slotService.UpdateStatusAsync(id, model.Status)));
    }
}
