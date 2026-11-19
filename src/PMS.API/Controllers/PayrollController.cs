using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.DutySchedule;
using PMS.Application.Services.Impl;

namespace PMS.API.Controllers;

/// <summary>Staff and Managers: salary received, and a simulated payday for demos.</summary>
[Route("api/payroll")]
[Authorize(Roles = "Staff,Manager")]
public class PayrollController(DutyScheduleService duty) : ApiController
{
    /// <summary>Payslips paid to the signed-in person (real and simulated), newest first.</summary>
    [HttpGet("mine")]
    public async Task<IActionResult> Mine() => Ok(ApiResult<IReadOnlyList<PaySlipModel>>.Success(await duty.MyPayslipsAsync()));

    /// <summary>Whether the simulate button is available.</summary>
    [HttpGet("simulation")]
    public IActionResult Simulation() => Ok(ApiResult<object>.Success(new { enabled = duty.SimulationAllowed }));

    /// <summary>Creates a paid, simulated payslip for the signed-in person: base salary and/or overtime hours.</summary>
    [HttpPost("simulate")]
    public async Task<IActionResult> Simulate(SimulatePayInput input) => Ok(ApiResult<PaySlipModel>.Success(await duty.SimulatePaydayAsync(input)));

    /// <summary>One of the signed-in person's payslips.</summary>
    [HttpGet("slips/{id:long}")]
    public async Task<IActionResult> Slip(long id) => Ok(ApiResult<PaySlipModel>.Success(await duty.SlipAsync(id)));
}

/// <summary>Admin: the month's payslips of everyone, paying a finished month, adjustments.</summary>
[Route("api/admin/payroll")]
[Authorize(Roles = "Admin")]
public class AdminPayrollController(DutyScheduleService duty) : ApiController
{
    /// <summary>Payslips of a month (yyyy-MM-01); a month still running shows estimates. Simulated payslips are included unless asked not to.</summary>
    [HttpGet]
    public async Task<IActionResult> Report([FromQuery] DateOnly month, [FromQuery] int? lotId, [FromQuery] string kind,
        [FromQuery] string status, [FromQuery] bool includeSimulated = true) =>
        Ok(ApiResult<PayrollReportModel>.Success(await duty.PayrollReportAsync(month, lotId, kind, status, includeSimulated)));

    /// <summary>Pays every unpaid payslip of a finished month.</summary>
    [HttpPost("pay-all")]
    public async Task<IActionResult> PayAll(PayAllInput input) => Ok(ApiResult<PayAllResultModel>.Success(await duty.PayAllAsync(input)));

    [HttpGet("slips/{id:long}")]
    public async Task<IActionResult> Slip(long id) => Ok(ApiResult<PaySlipModel>.Success(await duty.SlipAsync(id)));

    /// <summary>Adds an amount (+ or −) with a reason to a month that is not paid yet.</summary>
    [HttpPost("adjustments")]
    public async Task<IActionResult> Adjust(PayAdjustmentInput input) => Ok(ApiResult<PayrollReportModel>.Success(await duty.AddAdjustmentAsync(input)));
}
