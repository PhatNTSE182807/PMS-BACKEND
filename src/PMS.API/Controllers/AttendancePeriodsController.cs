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
    /// <summary>Every attendance period across all lots, with the confirmation state of each lot.</summary>
    [HttpGet("directory"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Directory() => Ok(ApiResult<object>.Success(await schedules.AttendancePeriodDirectoryAsync()));
    /// <summary>Attendance periods of a lot (or of every lot the caller can see).</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? lotId) => Ok(ApiResult<object>.Success(await schedules.AttendancePeriodsAsync(lotId)));
    /// <summary>Opens a new attendance period (a date range) for the lots.</summary>
    [HttpPost, Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(CreateAttendancePeriodInput input) => Ok(ApiResult<object>.Success(await schedules.CreateAttendancePeriodAsync(input)));
    /// <summary>Attendance of one lot in a period: worked hours, overtime and open issues per employee.</summary>
    [HttpGet("{id:int}/lots/{lotId:int}")]
    public async Task<IActionResult> Review(int id, int lotId) => Ok(ApiResult<object>.Success(await schedules.ReviewPeriodAsync(id, lotId)));
    /// <summary>The lot's Manager confirms the lot's attendance for the period.</summary>
    [HttpPost("{id:int}/lots/{lotId:int}/confirm"), Authorize(Roles = "Manager")]
    public async Task<IActionResult> Confirm(int id, int lotId, PeriodActionInput input) => Ok(ApiResult<object>.Success(await schedules.ConfirmAttendancePeriodAsync(id, lotId, input)));
    /// <summary>Locks a period so its attendance can feed payroll; no more changes after this.</summary>
    [HttpPost("{id:int}/lock"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Lock(int id, PeriodActionInput input) => Ok(ApiResult<object>.Success(await schedules.LockAttendancePeriodAsync(id, input)));
    /// <summary>Carries hours from an earlier period into this period of the lot for an employee, with a reason.</summary>
    [HttpPost("lots/{periodLotId:int}/carry-adjustments"), Authorize(Roles = "Manager")]
    public async Task<IActionResult> Carry(int periodLotId, CarryAdjustmentInput input) => Ok(ApiResult<object>.Success(await schedules.AddCarryAdjustmentAsync(periodLotId, input)));
    /// <summary>Records what a Manager did on a work day at a lot (the kind of day), with a reason.</summary>
    [HttpPut("manager-days/lots/{lotId:int}"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> ManagerDay(int lotId, ManagerWorkDayInput input) => Ok(ApiResult<object>.Success(await schedules.SaveManagerWorkDayAsync(lotId, input)));
}
