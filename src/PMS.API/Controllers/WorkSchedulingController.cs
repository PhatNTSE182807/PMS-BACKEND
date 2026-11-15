using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.WorkScheduling;
using PMS.Application.Services.Impl;

namespace PMS.API.Controllers;

[Route("api/manager/lots/{lotId:int}/work-scheduling")]
[Authorize(Roles = "Manager")]
public class WorkSchedulingController(WorkSchedulingService schedules) : ApiController
{
    [HttpGet("templates")]
    public async Task<IActionResult> Templates(int lotId) => Ok(ApiResult<object>.Success(await schedules.TemplatesAsync(lotId)));

    [HttpPost("templates")]
    public async Task<IActionResult> CreateTemplate(int lotId, WorkShiftTemplateInput input) =>
        Ok(ApiResult<object>.Success(await schedules.SaveTemplateAsync(lotId, null, input)));

    [HttpPut("templates/{id:int}")]
    public async Task<IActionResult> UpdateTemplate(int lotId, int id, WorkShiftTemplateInput input) =>
        Ok(ApiResult<object>.Success(await schedules.SaveTemplateAsync(lotId, id, input)));

    [HttpGet("staff")]
    public async Task<IActionResult> Staff(int lotId) => Ok(ApiResult<object>.Success(await schedules.StaffAsync(lotId)));

    [HttpGet("rosters")]
    public async Task<IActionResult> Rosters(int lotId) => Ok(ApiResult<object>.Success(await schedules.RostersAsync(lotId)));

    [HttpPost("rosters")]
    public async Task<IActionResult> CreateRoster(int lotId, CreateDutyRosterInput input) =>
        Ok(ApiResult<object>.Success(await schedules.CreateRosterAsync(lotId, input)));

    [HttpPost("rosters/{rosterId:int}/shifts")]
    public async Task<IActionResult> AddShift(int lotId, int rosterId, RosterShiftInput input) =>
        Ok(ApiResult<object>.Success(await schedules.SaveShiftAsync(lotId, rosterId, null, input)));

    [HttpPut("rosters/{rosterId:int}/shifts/{shiftId:int}")]
    public async Task<IActionResult> UpdateShift(int lotId, int rosterId, int shiftId, RosterShiftInput input) =>
        Ok(ApiResult<object>.Success(await schedules.SaveShiftAsync(lotId, rosterId, shiftId, input)));

    // POST cancellation preserves all assignments and their history.
    [HttpPost("rosters/{rosterId:int}/shifts/{shiftId:int}/cancel")]
    public async Task<IActionResult> RemoveShift(int lotId, int rosterId, int shiftId, RosterMutationInput input) =>
        Ok(ApiResult<object>.Success(await schedules.RemoveShiftAsync(lotId, rosterId, shiftId, input)));

    [HttpPost("rosters/{rosterId:int}/shifts/{shiftId:int}/assignments")]
    public async Task<IActionResult> AddAssignment(int lotId, int rosterId, int shiftId, DutyAssignmentInput input) =>
        Ok(ApiResult<object>.Success(await schedules.SaveAssignmentAsync(lotId, rosterId, shiftId, null, input)));

    [HttpPut("rosters/{rosterId:int}/shifts/{shiftId:int}/assignments/{id:int}")]
    public async Task<IActionResult> UpdateAssignment(int lotId, int rosterId, int shiftId, int id, DutyAssignmentInput input) =>
        Ok(ApiResult<object>.Success(await schedules.SaveAssignmentAsync(lotId, rosterId, shiftId, id, input)));

    [HttpPost("rosters/{rosterId:int}/shifts/{shiftId:int}/assignments/{id:int}/cancel")]
    public async Task<IActionResult> RemoveAssignment(int lotId, int rosterId, int shiftId, int id, RosterMutationInput input) =>
        Ok(ApiResult<object>.Success(await schedules.RemoveAssignmentAsync(lotId, rosterId, shiftId, id, input)));

    [HttpPost("rosters/{rosterId:int}/publish")]
    public async Task<IActionResult> Publish(int lotId, int rosterId, RosterMutationInput input) =>
        Ok(ApiResult<object>.Success(await schedules.PublishAsync(lotId, rosterId, input)));

    [HttpPost("rosters/{rosterId:int}/calendar-assignments")]
    public async Task<IActionResult> CalendarAssignment(int lotId, int rosterId, CalendarAssignmentInput input) =>
        Ok(ApiResult<object>.Success(await schedules.AssignFromCalendarAsync(lotId, rosterId, input)));

    [HttpGet("rosters/{rosterId:int}/eligible-staff")]
    public async Task<IActionResult> EligibleStaff(int lotId, int rosterId, [FromQuery] int? shiftId,
        [FromQuery] DateOnly workDate, [FromQuery] int? workShiftTemplateId, [FromQuery] int? excludeAssignmentId) =>
        Ok(ApiResult<object>.Success(await schedules.EligibleStaffAsync(lotId, rosterId, shiftId, workDate, workShiftTemplateId, excludeAssignmentId)));

    [HttpGet("rosters/{rosterId:int}/history")]
    public async Task<IActionResult> History(int lotId, int rosterId) => Ok(ApiResult<object>.Success(await schedules.HistoryAsync(lotId, rosterId)));
}

[Route("api/staff/work-schedule")]
[Authorize(Roles = "Staff")]
public class StaffWorkScheduleController(WorkSchedulingService schedules) : ApiController
{
    [HttpGet]
    public async Task<IActionResult> Mine([FromQuery] DateOnly from, [FromQuery] DateOnly to) =>
        Ok(ApiResult<object>.Success(await schedules.MyScheduleAsync(from, to)));
}
