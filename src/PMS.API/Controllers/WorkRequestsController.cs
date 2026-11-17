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
    /// <summary>Leave, swap and open-shift requests of a lot for a year, with leave balances.</summary>
    [HttpGet("lots/{lotId:int}/requests")]
    public async Task<IActionResult> Board(int lotId, [FromQuery] int year) => Ok(ApiResult<object>.Success(await schedules.RequestsAsync(lotId, year)));
    /// <summary>Requests linked to one shift assignment.</summary>
    [HttpGet("assignments/{id:int}/requests")]
    public async Task<IActionResult> Assignment(int id) => Ok(ApiResult<object>.Success(await schedules.AssignmentRequestsAsync(id)));
    /// <summary>Asks for leave.</summary>
    [HttpPost("leave-requests")]
    public async Task<IActionResult> Leave(CreateLeaveInput input) => Ok(ApiResult<object>.Success(await schedules.CreateLeaveAsync(input)));
    /// <summary>Acts on a leave request (Action: Approve, Reject or Cancel).</summary>
    [HttpPost("leave-requests/{id:int}/actions")]
    public async Task<IActionResult> LeaveAction(int id, WorkRequestCommand input) => Ok(ApiResult<object>.Success(await schedules.DecideLeaveAsync(id, input)));
    /// <summary>Asks a colleague to swap shifts.</summary>
    [HttpPost("swap-requests")]
    public async Task<IActionResult> Swap(CreateSwapInput input) => Ok(ApiResult<object>.Success(await schedules.CreateSwapAsync(input)));
    /// <summary>Acts on a swap request (Action: Accept, Approve, Reject or Cancel).</summary>
    [HttpPost("swap-requests/{id:int}/actions")]
    public async Task<IActionResult> SwapAction(int id, WorkRequestCommand input) => Ok(ApiResult<object>.Success(await schedules.DecideSwapAsync(id, input)));
    /// <summary>One open shift with its applicants.</summary>
    [HttpGet("open-shifts/{id:int}")]
    public async Task<IActionResult> OpenShift(int id) => Ok(ApiResult<object>.Success(await schedules.OpenShiftDetailAsync(id)));
    /// <summary>Acts on an open shift (Action: Claim, Withdraw, RejectClaim, Confirm, Assign, ManagerCover or Close).</summary>
    [HttpPost("open-shifts/{id:int}/actions")]
    public async Task<IActionResult> OpenShiftAction(int id, WorkRequestCommand input) => Ok(ApiResult<object>.Success(await schedules.ActOnOpenShiftAsync(id, input)));
    /// <summary>The Manager publishes an open shift that Staff of the lot can apply for.</summary>
    [Authorize(Roles = "Manager")]
    [HttpPost("lots/{lotId:int}/open-shifts")]
    public async Task<IActionResult> CreateOpenShift(int lotId, CreateOpenShiftInput input) => Ok(ApiResult<object>.Success(await schedules.CreateOpenShiftAsync(lotId, input)));
}
[Route("api/admin/work-scheduling/leave-balances")]
[Authorize(Roles = "Admin")]
public class WorkLeaveBalancesController(WorkSchedulingService schedules) : ApiController
{
    /// <summary>Leave balances of every employee for a year.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int year) => Ok(ApiResult<object>.Success(await schedules.AdminLeaveBalancesAsync(year)));
    /// <summary>Sets an employee's leave entitlement for a year.</summary>
    [HttpPut("{employeeId:int}/{year:int}")]
    public async Task<IActionResult> Update(int employeeId, int year, LeaveEntitlementInput input) => Ok(ApiResult<object>.Success(await schedules.SetLeaveEntitlementAsync(employeeId, year, input)));
}
