using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.DutySchedule;
using PMS.Application.Services.Impl;

namespace PMS.API.Controllers;

/// <summary>The lot's Manager: duty board (4 shifts a day), replacements, leave decisions and attendance corrections.</summary>
[Route("api/manager/lots/{lotId:int}/duty")]
[Authorize(Roles = "Manager")]
public class ManagerDutyController(DutyScheduleService duty) : ApiController
{
    /// <summary>One week of the board (Monday to Sunday) with the lot's Staff.</summary>
    [HttpGet("week")]
    public async Task<IActionResult> Week(int lotId, [FromQuery] DateOnly? start) =>
        Ok(ApiResult<DutyWeekModel>.Success(await duty.ManagerWeekAsync(lotId, start)));

    /// <summary>Who can take a shift, with the reason someone cannot.</summary>
    [HttpGet("candidates")]
    public async Task<IActionResult> Candidates(int lotId, [FromQuery] DateOnly date, [FromQuery] int slot, [FromQuery] int? excludeEmployeeId) =>
        Ok(ApiResult<IReadOnlyList<DutyCandidateModel>>.Success(await duty.CandidatesAsync(lotId, date, slot, excludeEmployeeId)));

    /// <summary>Puts someone on a shift (or leaves it empty) for that date only or for every week from that date.</summary>
    [HttpPut("cells")]
    public async Task<IActionResult> SetCell(int lotId, DutyCellInput input) =>
        Ok(ApiResult<DutyWeekModel>.Success(await duty.SetCellAsync(lotId, input)));

    /// <summary>Records the real check-in / check-out of a shift, or an absence, with a reason.</summary>
    [HttpPut("attendance")]
    public async Task<IActionResult> Attendance(int lotId, DutyAttendanceInput input) =>
        Ok(ApiResult<DutyWeekModel>.Success(await duty.AdjustAttendanceAsync(lotId, input)));

    /// <summary>Leave requests of the lot: waiting ones first, with the shifts that need someone else.</summary>
    [HttpGet("leave-requests")]
    public async Task<IActionResult> Leave(int lotId) =>
        Ok(ApiResult<IReadOnlyList<LeaveRequestModel>>.Success(await duty.LotLeaveAsync(lotId)));

    /// <summary>Approves a leave request and puts the chosen people on its shifts (shifts without one stay empty).</summary>
    [HttpPost("leave-requests/{id:int}/approve")]
    public async Task<IActionResult> Approve(int lotId, int id, LeaveDecisionInput input) =>
        Ok(ApiResult<IReadOnlyList<LeaveRequestModel>>.Success(await duty.ApproveLeaveAsync(lotId, id, input)));

    /// <summary>Rejects a leave request with a reason.</summary>
    [HttpPost("leave-requests/{id:int}/reject")]
    public async Task<IActionResult> Reject(int lotId, int id, LeaveDecisionInput input) =>
        Ok(ApiResult<IReadOnlyList<LeaveRequestModel>>.Success(await duty.RejectLeaveAsync(lotId, id, input)));
}

/// <summary>The signed-in Staff member: their lot's duty board, starting and ending shifts, leave.</summary>
[Route("api/staff/duty")]
[Authorize(Roles = "Staff")]
public class StaffDutyController(DutyScheduleService duty) : ApiController
{
    /// <summary>Whether the Staff is on shift (the POS is open), the shift they can start and the next one.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status() => Ok(ApiResult<DutyStatusModel>.Success(await duty.StatusAsync()));

    /// <summary>One week of the lot's board, read only.</summary>
    [HttpGet("week")]
    public async Task<IActionResult> Week([FromQuery] DateOnly? start) => Ok(ApiResult<DutyWeekModel>.Success(await duty.StaffWeekAsync(start)));

    /// <summary>Checks in to the shift that is open now (from 30 minutes before it starts).</summary>
    [HttpPost("shifts/start")]
    public async Task<IActionResult> Start() => Ok(ApiResult<DutyStatusModel>.Success(await duty.StartShiftAsync()));

    /// <summary>Checks out of the current shift.</summary>
    [HttpPost("shifts/end")]
    public async Task<IActionResult> End() => Ok(ApiResult<DutyStatusModel>.Success(await duty.EndShiftAsync()));

    /// <summary>The Staff's leave requests and paid leave left this year.</summary>
    [HttpGet("leave")]
    public async Task<IActionResult> Leave() => Ok(ApiResult<MyLeaveModel>.Success(await duty.MyLeaveAsync()));

    /// <summary>The shifts a leave would cover and its paid / unpaid days, or why it cannot be sent.</summary>
    [HttpGet("leave/preview")]
    public async Task<IActionResult> Preview([FromQuery] DateOnly from, [FromQuery] DateOnly to) =>
        Ok(ApiResult<LeavePreviewModel>.Success(await duty.PreviewLeaveAsync(from, to)));

    /// <summary>Asks the Manager for leave.</summary>
    [HttpPost("leave")]
    public async Task<IActionResult> Request(LeaveRequestInput input) => Ok(ApiResult<LeaveRequestModel>.Success(await duty.RequestLeaveAsync(input)));

    /// <summary>Withdraws a request the Manager has not decided yet.</summary>
    [HttpPost("leave/{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id) => Ok(ApiResult<MyLeaveModel>.Success(await duty.CancelLeaveAsync(id)));
}

/// <summary>Admin: yearly paid leave allowance of every Staff member.</summary>
[Route("api/admin/duty/leave-balances")]
[Authorize(Roles = "Admin")]
public class AdminLeaveBalancesController(DutyScheduleService duty) : ApiController
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int year) =>
        Ok(ApiResult<IReadOnlyList<LeaveBalanceRowModel>>.Success(await duty.LeaveBalancesAsync(year)));

    [HttpPut("{employeeId:int}/{year:int}")]
    public async Task<IActionResult> Set(int employeeId, int year, LeaveBalanceInput input) =>
        Ok(ApiResult<IReadOnlyList<LeaveBalanceRowModel>>.Success(await duty.SetLeaveBalanceAsync(employeeId, year, input)));
}
