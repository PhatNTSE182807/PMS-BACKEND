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
    [HttpGet("directory"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Directory() => Ok(ApiResult<object>.Success(await payroll.PayrollDirectoryAsync()));
    [HttpGet("profiles"), Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> Profiles([FromQuery] int? lotId) => Ok(ApiResult<object>.Success(await payroll.PayrollProfilesAsync(lotId)));
    [HttpPost("profiles"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateProfile(PayrollProfileInput input) => Ok(ApiResult<object>.Success(await payroll.SavePayrollProfileAsync(null, input)));
    [HttpPut("profiles/{id:int}"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Profile(int id, PayrollProfileInput input) => Ok(ApiResult<object>.Success(await payroll.SavePayrollProfileAsync(id, input)));
    [HttpGet("sources"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Sources([FromQuery] int lotId) => Ok(ApiResult<object>.Success(await payroll.PayrollSourcesAsync(lotId)));
    [HttpPost("sources/{periodLotId:int}/seal"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Seal(int periodLotId, SealPayrollSourceInput input) => Ok(ApiResult<object>.Success(await payroll.SealPayrollSourceAsync(periodLotId, input)));
    [HttpPost("preview"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Preview(PayrollGenerateInput input) => Ok(ApiResult<object>.Success(await payroll.PreviewPayrollAsync(input)));
    [HttpPost("generate"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Generate(PayrollGenerateInput input) => Ok(ApiResult<object>.Success(await payroll.GeneratePayrollAsync(input)));
    [HttpGet("slips")]
    public async Task<IActionResult> Slips([FromQuery] int? lotId, [FromQuery] DateOnly? month, [FromQuery] bool mine = false) => Ok(ApiResult<object>.Success(await payroll.PayrollSlipsAsync(lotId, month, mine)));
    [HttpGet("slips/{id:int}")]
    public async Task<IActionResult> Slip(int id) => Ok(ApiResult<object>.Success(await payroll.PayrollSlipAsync(id)));
    [HttpGet("slips/{id:int}/lines/{lineId:int}/source")]
    public async Task<IActionResult> Source(int id, int lineId) => Ok(ApiResult<object>.Success(await payroll.PayrollLineSourceAsync(id, lineId)));
    [HttpPost("slips/{id:int}/approve"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Approve(int id, PayrollActionInput input) => Ok(ApiResult<object>.Success(await payroll.ActOnPayrollAsync(id, "approve", input)));
    [HttpPost("slips/{id:int}/paid"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Paid(int id, PayrollActionInput input) => Ok(ApiResult<object>.Success(await payroll.ActOnPayrollAsync(id, "paid", input)));
    [HttpGet("adjustments"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Adjustments([FromQuery] int lotId, [FromQuery] DateOnly month) => Ok(ApiResult<object>.Success(await payroll.PayrollAdjustmentsAsync(lotId, month)));
    [HttpPost("adjustments"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Adjustment(PayrollAdjustmentInput input) => Ok(ApiResult<object>.Success(await payroll.AddPayrollAdjustmentAsync(input)));
    [HttpPost("slips/{id:int}/complaints"), Authorize(Roles = "Staff,Manager")]
    public async Task<IActionResult> Complain(int id, PayrollComplaintInput input) => Ok(ApiResult<object>.Success(await payroll.ComplainPayrollAsync(id, input)));
    [HttpPost("complaints/{id:int}/resolve"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Resolve(int id, PayrollComplaintDecisionInput input) => Ok(ApiResult<object>.Success(await payroll.ResolvePayrollComplaintAsync(id, input)));
    [HttpGet("audit"), Authorize(Roles = "Admin")]
    public async Task<IActionResult> Audit([FromQuery] int? lotId) => Ok(ApiResult<object>.Success(await payroll.PayrollAuditAsync(lotId)));
}
