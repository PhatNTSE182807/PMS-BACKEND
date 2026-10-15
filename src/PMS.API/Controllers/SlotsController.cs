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

    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] CreateSlotModel model)
    {
        return Ok(ApiResult<SlotResponseModel>.Success(
            await slotService.CreateAsync(model)));
    }

    [HttpPost("/api/zones/{zoneId:int}/slots")]
    public async Task<IActionResult> CreateForZoneAsync(int zoneId, [FromBody] CreateSlotModel model)
    {
        model.ZoneId = zoneId;
        return Ok(ApiResult<SlotResponseModel>.Success(
            await slotService.CreateAsync(model)));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateAsync(int id, [FromBody] UpdateSlotModel model)
    {
        return Ok(ApiResult<SlotResponseModel>.Success(
            await slotService.UpdateAsync(id, model)));
    }

    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> UpdateStatusAsync(int id, [FromBody] UpdateSlotStatusModel model)
    {
        return Ok(ApiResult<bool>.Success(
            await slotService.UpdateStatusAsync(id, model.Status)));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteAsync(int id)
    {
        return Ok(ApiResult<bool>.Success(
            await slotService.DeleteAsync(id)));
    }
}
