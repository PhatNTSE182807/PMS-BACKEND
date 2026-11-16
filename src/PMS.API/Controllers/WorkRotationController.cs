using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.WorkScheduling;
using PMS.Application.Services.Impl;

namespace PMS.API.Controllers;
[Route("api/manager/lots/{lotId:int}/work-scheduling")]
[Authorize(Roles = "Manager")]
public class WorkRotationController(WorkSchedulingService schedules) : ApiController
{
    [HttpGet("rotation-groups")]
    public async Task<IActionResult> Groups(int lotId) => Ok(ApiResult<object>.Success(await schedules.RotationGroupsAsync(lotId)));
    [HttpPost("rotation-groups")]
    public async Task<IActionResult> CreateGroup(int lotId, RotationGroupInput input) => Ok(ApiResult<object>.Success(await schedules.SaveRotationGroupAsync(lotId, null, input)));
    [HttpPut("rotation-groups/{id:int}")]
    public async Task<IActionResult> UpdateGroup(int lotId, int id, RotationGroupInput input) => Ok(ApiResult<object>.Success(await schedules.SaveRotationGroupAsync(lotId, id, input)));
    [HttpGet("rotation-cycles")]
    public async Task<IActionResult> Cycles(int lotId) => Ok(ApiResult<object>.Success(await schedules.RotationCyclesAsync(lotId)));
    [HttpPost("rotation-cycles")]
    public async Task<IActionResult> CreateCycle(int lotId, RotationCycleInput input) => Ok(ApiResult<object>.Success(await schedules.SaveRotationCycleAsync(lotId, null, input)));
    [HttpPut("rotation-cycles/{id:int}")]
    public async Task<IActionResult> UpdateCycle(int lotId, int id, RotationCycleInput input) => Ok(ApiResult<object>.Success(await schedules.SaveRotationCycleAsync(lotId, id, input)));
    [HttpPost("rosters/{rosterId:int}/rotation-preview")]
    public async Task<IActionResult> Preview(int lotId, int rosterId, RotationGenerationInput input) => Ok(ApiResult<object>.Success(await schedules.PreviewRotationAsync(lotId, rosterId, input)));
    [HttpPost("rosters/{rosterId:int}/rotation-apply")]
    public async Task<IActionResult> Apply(int lotId, int rosterId, RotationGenerationInput input) => Ok(ApiResult<object>.Success(await schedules.ApplyRotationAsync(lotId, rosterId, input)));
    [HttpPost("rosters/{rosterId:int}/standby")]
    public async Task<IActionResult> AddStandby(int lotId, int rosterId, StandbyMutationInput input) => Ok(ApiResult<object>.Success(await schedules.SaveStandbyAsync(lotId, rosterId, null, input)));
    [HttpPut("rosters/{rosterId:int}/standby/{id:int}")]
    public async Task<IActionResult> EditStandby(int lotId, int rosterId, int id, StandbyMutationInput input) => Ok(ApiResult<object>.Success(await schedules.SaveStandbyAsync(lotId, rosterId, id, input)));
}
