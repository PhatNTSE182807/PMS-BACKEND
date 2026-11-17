using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.WorkScheduling;
using PMS.Application.Services.Impl;

namespace PMS.API.Controllers;
[Route("api/payroll")]
[Authorize(Roles = "Admin,Manager,Staff")]
public class PayrollController(WorkSchedulingService payroll) : ApiController
{
    /// <summary>Payroll overview of every lot: months, slip counts and states.</summary>
    [HttpGet("directory"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Directory() => Ok(ApiResult<object>.Success(await payroll.PayrollDirectoryAsync()));
    /// <summary>Pay profiles (base pay, hourly and overtime rates) of the employees of a lot or of every lot.</summary>
    [HttpGet("profiles"), Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> Profiles([FromQuery] int? lotId) => Ok(ApiResult<object>.Success(await payroll.PayrollProfilesAsync(lotId)));
    /// <summary>Creates a pay profile for an employee.</summary>
    [HttpPost("profiles"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateProfile(PayrollProfileInput input) => Ok(ApiResult<object>.Success(await payroll.SavePayrollProfileAsync(null, input)));
    /// <summary>Updates a pay profile.</summary>
    [HttpPut("profiles/{id:int}"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Profile(int id, PayrollProfileInput input) => Ok(ApiResult<object>.Success(await payroll.SavePayrollProfileAsync(id, input)));
    /// <summary>Hours carried between locked attendance months at a lot, to be valued as adjustments.</summary>
    [HttpGet("carries"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Carries([FromQuery] int lotId) => Ok(ApiResult<object>.Success(await payroll.PayrollCarriesAsync(lotId)));
    /// <summary>Calculates payslips for a lot and month without saving them.</summary>
    [HttpPost("preview"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Preview(PayrollGenerateInput input) => Ok(ApiResult<object>.Success(await payroll.PreviewPayrollAsync(input)));
    /// <summary>Creates the payslips of a lot and month from the sealed attendance.</summary>
    [HttpPost("generate"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Generate(PayrollGenerateInput input) => Ok(ApiResult<object>.Success(await payroll.GeneratePayrollAsync(input)));
    /// <summary>Payslips of a lot and month; with mine=true only the caller's own payslips.</summary>
    [HttpGet("slips")]
    public async Task<IActionResult> Slips([FromQuery] int? lotId, [FromQuery] DateOnly? month, [FromQuery] bool mine = false) => Ok(ApiResult<object>.Success(await payroll.PayrollSlipsAsync(lotId, month, mine)));
    /// <summary>One payslip with its lines.</summary>
    [HttpGet("slips/{id:int}")]
    public async Task<IActionResult> Slip(int id) => Ok(ApiResult<object>.Success(await payroll.PayrollSlipAsync(id)));
    /// <summary>The attendance records behind one payslip line.</summary>
    [HttpGet("slips/{id:int}/lines/{lineId:int}/source")]
    public async Task<IActionResult> Source(int id, int lineId) => Ok(ApiResult<object>.Success(await payroll.PayrollLineSourceAsync(id, lineId)));
    /// <summary>Approves a payslip.</summary>
    [HttpPost("slips/{id:int}/approve"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Approve(int id, PayrollActionInput input) => Ok(ApiResult<object>.Success(await payroll.ActOnPayrollAsync(id, "approve", input)));
    /// <summary>Marks an approved payslip as paid.</summary>
    [HttpPost("slips/{id:int}/paid"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Paid(int id, PayrollActionInput input) => Ok(ApiResult<object>.Success(await payroll.ActOnPayrollAsync(id, "paid", input)));
    /// <summary>Manual payroll adjustments (bonuses, deductions) of a lot and month.</summary>
    [HttpGet("adjustments"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Adjustments([FromQuery] int lotId, [FromQuery] DateOnly month) => Ok(ApiResult<object>.Success(await payroll.PayrollAdjustmentsAsync(lotId, month)));
    /// <summary>Adds a manual payroll adjustment for an employee.</summary>
    [HttpPost("adjustments"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Adjustment(PayrollAdjustmentInput input) => Ok(ApiResult<object>.Success(await payroll.AddPayrollAdjustmentAsync(input)));
    /// <summary>An employee disputes their payslip.</summary>
    [HttpPost("slips/{id:int}/complaints"), Authorize(Roles = "Staff,Manager")]
    public async Task<IActionResult> Complain(int id, PayrollComplaintInput input) => Ok(ApiResult<object>.Success(await payroll.ComplainPayrollAsync(id, input)));
    /// <summary>Accepts or rejects a payslip complaint.</summary>
    [HttpPost("complaints/{id:int}/resolve"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Resolve(int id, PayrollComplaintDecisionInput input) => Ok(ApiResult<object>.Success(await payroll.ResolvePayrollComplaintAsync(id, input)));
    /// <summary>History of payroll actions (generate, approve, pay, adjust, complaints) of a lot or of every lot.</summary>
    [HttpGet("audit"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Audit([FromQuery] int? lotId) => Ok(ApiResult<object>.Success(await payroll.PayrollAuditAsync(lotId)));
}
