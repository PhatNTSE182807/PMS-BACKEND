using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.WorkScheduling;
using PMS.Application.Services.Impl;

namespace PMS.API.Controllers;
[Route("api/work-scheduling/attendance-periods")]
[Authorize(Roles = "Manager,Admin")]
public class AttendancePeriodsController(WorkSchedulingService schedules) : ApiController
{
    [HttpGet("directory"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Directory() => Ok(ApiResult<object>.Success(await schedules.AttendancePeriodDirectoryAsync()));
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? lotId) => Ok(ApiResult<object>.Success(await schedules.AttendancePeriodsAsync(lotId)));
    [HttpPost, Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(CreateAttendancePeriodInput input) => Ok(ApiResult<object>.Success(await schedules.CreateAttendancePeriodAsync(input)));
    [HttpGet("{id:int}/lots/{lotId:int}")]
    public async Task<IActionResult> Review(int id, int lotId) => Ok(ApiResult<object>.Success(await schedules.ReviewPeriodAsync(id, lotId)));
    [HttpPost("{id:int}/lots/{lotId:int}/confirm"), Authorize(Roles = "Manager")]
    public async Task<IActionResult> Confirm(int id, int lotId, PeriodActionInput input) => Ok(ApiResult<object>.Success(await schedules.ConfirmAttendancePeriodAsync(id, lotId, input)));
    [HttpPost("{id:int}/lock"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Lock(int id, PeriodActionInput input) => Ok(ApiResult<object>.Success(await schedules.LockAttendancePeriodAsync(id, input)));
    [HttpPost("lots/{periodLotId:int}/carry-adjustments"), Authorize(Roles = "Manager")]
    public async Task<IActionResult> Carry(int periodLotId, CarryAdjustmentInput input) => Ok(ApiResult<object>.Success(await schedules.AddCarryAdjustmentAsync(periodLotId, input)));
    [HttpPut("manager-days/lots/{lotId:int}"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> ManagerDay(int lotId, ManagerWorkDayInput input) => Ok(ApiResult<object>.Success(await schedules.SaveManagerWorkDayAsync(lotId, input)));
}
