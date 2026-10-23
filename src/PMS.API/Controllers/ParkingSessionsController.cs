using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Operations;
using PMS.Application.Services;
using PMS.Shared.Services;

namespace PMS.API.Controllers;

/// <summary>
/// The customer's own parking sessions: running fee and prepaying in the app (FR-PAY-07), and the
/// history of finished sessions with their e-receipts (FR-CUS-02).
/// </summary>
[Route("api/parking-sessions")]
[Authorize(Roles = "User")]
public class ParkingSessionsController(
    IGateService gateService,
    IParkingHistoryService historyService,
    IClaimService claimService) : ApiController
{
    [HttpGet("mine")]
    public async Task<IActionResult> GetMineAsync() =>
        Ok(ApiResult<IReadOnlyList<SessionResponseModel>>.Success(await gateService.GetMySessionsAsync(claimService.GetUserId())));

    /// <summary>
    /// Finished sessions (the vehicle left), newest exit first. Query: page (1-based), pageSize (1..50,
    /// default 10), from / to (exit time, UTC) and vehicleId.
    /// </summary>
    [HttpGet("history")]
    public async Task<IActionResult> GetHistoryAsync([FromQuery] ParkingHistoryQuery query) =>
        Ok(ApiResult<ParkingHistoryPageModel>.Success(await historyService.GetHistoryAsync(claimService.GetUserId(), query)));

    /// <summary>E-receipt of a finished session: fee breakdown, deposit and payments.</summary>
    [HttpGet("{id:long}/receipt")]
    public async Task<IActionResult> GetReceiptAsync(long id) =>
        Ok(ApiResult<ParkingReceiptModel>.Success(await historyService.GetReceiptAsync(claimService.GetUserId(), id)));

    /// <summary>
    /// Vehicles of the customer inside a lot now (newest entry first), each with the lot, bay, running fee,
    /// tariff, open VNPay / MoMo payment and exit pass. Empty when nothing is parked.
    /// </summary>
    [HttpGet("current")]
    public async Task<IActionResult> GetCurrentAsync() =>
        Ok(ApiResult<IReadOnlyList<CurrentSessionModel>>.Success(await gateService.GetMyCurrentSessionsAsync(claimService.GetUserId())));

    /// <summary>Exit pass of a parked vehicle: every checked-in session has one; the exit lane scans it.</summary>
    [HttpGet("{id:long}/exit-pass")]
    public async Task<IActionResult> GetExitPassAsync(long id) =>
        Ok(ApiResult<ExitPassModel>.Success(await gateService.GetMyExitPassAsync(claimService.GetUserId(), id)));

    /// <summary>Fee so far after the deposit (does not change the session).</summary>
    [HttpGet("{id:long}/quote")]
    public async Task<IActionResult> QuoteAsync(long id) =>
        Ok(ApiResult<CheckoutQuoteModel>.Success(await gateService.QuoteMySessionAsync(claimService.GetUserId(), id)));

    /// <summary>Prepay with VNPay or MoMo; once paid an Exit Window and a signed Exit Pass are issued.</summary>
    [HttpPost("{id:long}/payments")]
    public async Task<IActionResult> PayAsync(long id, [FromBody] CreatePaymentModel model) =>
        Ok(ApiResult<PaymentResponseModel>.Success(
            await gateService.PayMySessionAsync(claimService.GetUserId(), id, model, PaymentsController.RequestContext(HttpContext))));
}
