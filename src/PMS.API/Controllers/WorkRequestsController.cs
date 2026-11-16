using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.WorkScheduling;
using PMS.Application.Services.Impl;

namespace PMS.API.Controllers;
[Route("api/work-scheduling")]
[Authorize(Roles = "Manager,Staff")]
public class WorkRequestsController(WorkSchedulingService schedules) : ApiController
{
    [HttpGet("lots/{lotId:int}/requests")]
    public async Task<IActionResult> Board(int lotId, [FromQuery] int year) => Ok(ApiResult<object>.Success(await schedules.RequestsAsync(lotId, year)));
    [HttpGet("assignments/{id:int}/requests")]
    public async Task<IActionResult> Assignment(int id) => Ok(ApiResult<object>.Success(await schedules.AssignmentRequestsAsync(id)));
    [HttpPost("leave-requests")]
    public async Task<IActionResult> Leave(CreateLeaveInput input) => Ok(ApiResult<object>.Success(await schedules.CreateLeaveAsync(input)));
    [HttpPost("leave-requests/{id:int}/actions")]
    public async Task<IActionResult> LeaveAction(int id, WorkRequestCommand input) => Ok(ApiResult<object>.Success(await schedules.DecideLeaveAsync(id, input)));
    [HttpPost("swap-requests")]
    public async Task<IActionResult> Swap(CreateSwapInput input) => Ok(ApiResult<object>.Success(await schedules.CreateSwapAsync(input)));
    [HttpPost("swap-requests/{id:int}/actions")]
    public async Task<IActionResult> SwapAction(int id, WorkRequestCommand input) => Ok(ApiResult<object>.Success(await schedules.DecideSwapAsync(id, input)));
    [HttpGet("open-shifts/{id:int}")]
    public async Task<IActionResult> OpenShift(int id) => Ok(ApiResult<object>.Success(await schedules.OpenShiftDetailAsync(id)));
    [HttpPost("open-shifts/{id:int}/actions")]
    public async Task<IActionResult> OpenShiftAction(int id, WorkRequestCommand input) => Ok(ApiResult<object>.Success(await schedules.ActOnOpenShiftAsync(id, input)));
    [Authorize(Roles = "Manager")]
    [HttpPost("lots/{lotId:int}/open-shifts")]
    public async Task<IActionResult> CreateOpenShift(int lotId, CreateOpenShiftInput input) => Ok(ApiResult<object>.Success(await schedules.CreateOpenShiftAsync(lotId, input)));
}
[Route("api/admin/work-scheduling/leave-balances")]
[Authorize(Roles = "Admin")]
public class WorkLeaveBalancesController(WorkSchedulingService schedules) : ApiController
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int year) => Ok(ApiResult<object>.Success(await schedules.AdminLeaveBalancesAsync(year)));
    [HttpPut("{employeeId:int}/{year:int}")]
    public async Task<IActionResult> Update(int employeeId, int year, LeaveEntitlementInput input) => Ok(ApiResult<object>.Success(await schedules.SetLeaveEntitlementAsync(employeeId, year, input)));
}
