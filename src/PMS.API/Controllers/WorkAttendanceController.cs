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
    /// <summary>The Manager marks a finished shift's attendance as reviewed (an auto-ended shift keeps its end time, a missed one counts as absent).</summary>
    [HttpPost("review"), Authorize(Roles = "Manager")]
    public async Task<IActionResult> Review(int assignmentId, AttendanceMutationInput input) =>
        Ok(ApiResult<AttendanceDetailModel>.Success(await schedules.ReviewAssignmentAsync(assignmentId, input)));

    /// <summary>The Manager approves or rejects the overtime of a shift.</summary>
    [HttpPost("overtime"), Authorize(Roles = "Manager")]
    public async Task<IActionResult> Overtime(int assignmentId, OvertimeDecisionInput input) =>
        Ok(ApiResult<AttendanceDetailModel>.Success(await schedules.DecideOvertimeAsync(assignmentId, input)));
    /// <summary>Attendance of one shift assignment: check-in, check-out, breaks and overtime.</summary>
    [HttpGet]
    public async Task<IActionResult> Detail(int assignmentId) => Ok(ApiResult<AttendanceDetailModel>.Success(await schedules.AttendanceDetailAsync(assignmentId)));

    /// <summary>The Staff member starts their assigned shift (clock in).</summary>
    [HttpPost("start")]
    [Authorize(Roles = "Staff")]
    public async Task<IActionResult> Start(int assignmentId, AttendanceMutationInput input) =>
        Ok(ApiResult<AttendanceDetailModel>.Success(await schedules.StartAssignmentAsync(assignmentId, input)));

    /// <summary>The Staff member ends their assigned shift (clock out).</summary>
    [HttpPost("end")]
    [Authorize(Roles = "Staff")]
    public async Task<IActionResult> End(int assignmentId, AttendanceMutationInput input) =>
        Ok(ApiResult<AttendanceDetailModel>.Success(await schedules.EndAssignmentAsync(assignmentId, input)));

    /// <summary>The Manager reopens a closed shift so its attendance can be corrected.</summary>
    [HttpPost("reopen")]
    [Authorize(Roles = "Manager")]
    public async Task<IActionResult> Reopen(int assignmentId, AttendanceMutationInput input) =>
        Ok(ApiResult<AttendanceDetailModel>.Success(await schedules.ReopenAssignmentAsync(assignmentId, input)));

    /// <summary>The Manager corrects a shift's recorded times, with a reason.</summary>
    [HttpPut]
    [Authorize(Roles = "Manager")]
    public async Task<IActionResult> Adjust(int assignmentId, AttendanceAdjustmentInput input) =>
        Ok(ApiResult<AttendanceDetailModel>.Success(await schedules.AdjustAttendanceAsync(assignmentId, input)));
}

[Route("api/staff/current-shifts")]
[Authorize(Roles = "Staff")]
public class CurrentWorkShiftsController(WorkSchedulingService schedules) : ApiController
{
    /// <summary>The signed-in Staff member's shifts from yesterday to two days ahead, with their attendance.</summary>
    [HttpGet]
    public async Task<IActionResult> Current() => Ok(ApiResult<IReadOnlyList<AttendanceDetailModel>>.Success(await schedules.CurrentAssignmentsAsync()));
}
