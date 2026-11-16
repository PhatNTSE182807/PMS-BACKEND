using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.WorkScheduling;
using PMS.Application.Services.Impl;

namespace PMS.API.Controllers;

[Route("api/work-scheduling/assignments/{assignmentId:int}/attendance")]
[Authorize(Roles = "Manager,Staff")]
public class WorkAttendanceController(WorkSchedulingService schedules) : ApiController
{
    [HttpPost("review"), Authorize(Roles = "Manager")]
    public async Task<IActionResult> Review(int assignmentId, AttendanceMutationInput input) =>
        Ok(ApiResult<AttendanceDetailModel>.Success(await schedules.ReviewAssignmentAsync(assignmentId, input)));

    [HttpPost("overtime"), Authorize(Roles = "Manager")]
    public async Task<IActionResult> Overtime(int assignmentId, OvertimeDecisionInput input) =>
        Ok(ApiResult<AttendanceDetailModel>.Success(await schedules.DecideOvertimeAsync(assignmentId, input)));
    [HttpGet]
    public async Task<IActionResult> Detail(int assignmentId) => Ok(ApiResult<AttendanceDetailModel>.Success(await schedules.AttendanceDetailAsync(assignmentId)));

    [HttpPost("start")]
    [Authorize(Roles = "Staff")]
    public async Task<IActionResult> Start(int assignmentId, AttendanceMutationInput input) =>
        Ok(ApiResult<AttendanceDetailModel>.Success(await schedules.StartAssignmentAsync(assignmentId, input)));

    [HttpPost("end")]
    [Authorize(Roles = "Staff")]
    public async Task<IActionResult> End(int assignmentId, AttendanceMutationInput input) =>
        Ok(ApiResult<AttendanceDetailModel>.Success(await schedules.EndAssignmentAsync(assignmentId, input)));

    [HttpPost("reopen")]
    [Authorize(Roles = "Manager")]
    public async Task<IActionResult> Reopen(int assignmentId, AttendanceMutationInput input) =>
        Ok(ApiResult<AttendanceDetailModel>.Success(await schedules.ReopenAssignmentAsync(assignmentId, input)));

    [HttpPut]
    [Authorize(Roles = "Manager")]
    public async Task<IActionResult> Adjust(int assignmentId, AttendanceAdjustmentInput input) =>
        Ok(ApiResult<AttendanceDetailModel>.Success(await schedules.AdjustAttendanceAsync(assignmentId, input)));
}

[Route("api/staff/current-shifts")]
[Authorize(Roles = "Staff")]
public class CurrentWorkShiftsController(WorkSchedulingService schedules) : ApiController
{
    [HttpGet]
    public async Task<IActionResult> Current() => Ok(ApiResult<IReadOnlyList<AttendanceDetailModel>>.Success(await schedules.CurrentAssignmentsAsync()));
}
