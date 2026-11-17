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
    /// <summary>Rotation groups (teams that rotate through shifts) of the lot.</summary>
    [HttpGet("rotation-groups")]
    public async Task<IActionResult> Groups(int lotId) => Ok(ApiResult<object>.Success(await schedules.RotationGroupsAsync(lotId)));
    /// <summary>Creates a rotation group.</summary>
    [HttpPost("rotation-groups")]
    public async Task<IActionResult> CreateGroup(int lotId, RotationGroupInput input) => Ok(ApiResult<object>.Success(await schedules.SaveRotationGroupAsync(lotId, null, input)));
    /// <summary>Updates a rotation group.</summary>
    [HttpPut("rotation-groups/{id:int}")]
    public async Task<IActionResult> UpdateGroup(int lotId, int id, RotationGroupInput input) => Ok(ApiResult<object>.Success(await schedules.SaveRotationGroupAsync(lotId, id, input)));
    /// <summary>Rotation cycles (repeating shift patterns) of the lot.</summary>
    [HttpGet("rotation-cycles")]
    public async Task<IActionResult> Cycles(int lotId) => Ok(ApiResult<object>.Success(await schedules.RotationCyclesAsync(lotId)));
    /// <summary>Creates a rotation cycle.</summary>
    [HttpPost("rotation-cycles")]
    public async Task<IActionResult> CreateCycle(int lotId, RotationCycleInput input) => Ok(ApiResult<object>.Success(await schedules.SaveRotationCycleAsync(lotId, null, input)));
    /// <summary>Updates a rotation cycle.</summary>
    [HttpPut("rotation-cycles/{id:int}")]
    public async Task<IActionResult> UpdateCycle(int lotId, int id, RotationCycleInput input) => Ok(ApiResult<object>.Success(await schedules.SaveRotationCycleAsync(lotId, id, input)));
    /// <summary>Shows the assignments a rotation would create in the roster, without saving them.</summary>
    [HttpPost("rosters/{rosterId:int}/rotation-preview")]
    public async Task<IActionResult> Preview(int lotId, int rosterId, RotationGenerationInput input) => Ok(ApiResult<object>.Success(await schedules.PreviewRotationAsync(lotId, rosterId, input)));
    /// <summary>Fills the roster with the assignments of a rotation.</summary>
    [HttpPost("rosters/{rosterId:int}/rotation-apply")]
    public async Task<IActionResult> Apply(int lotId, int rosterId, RotationGenerationInput input) => Ok(ApiResult<object>.Success(await schedules.ApplyRotationAsync(lotId, rosterId, input)));
    /// <summary>Adds a standby (on-call) employee to the roster.</summary>
    [HttpPost("rosters/{rosterId:int}/standby")]
    public async Task<IActionResult> AddStandby(int lotId, int rosterId, StandbyMutationInput input) => Ok(ApiResult<object>.Success(await schedules.SaveStandbyAsync(lotId, rosterId, null, input)));
    /// <summary>Updates a standby entry of the roster.</summary>
    [HttpPut("rosters/{rosterId:int}/standby/{id:int}")]
    public async Task<IActionResult> EditStandby(int lotId, int rosterId, int id, StandbyMutationInput input) => Ok(ApiResult<object>.Success(await schedules.SaveStandbyAsync(lotId, rosterId, id, input)));
}
