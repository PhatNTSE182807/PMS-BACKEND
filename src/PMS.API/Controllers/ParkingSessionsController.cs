using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Operations;
using PMS.Application.Services;
using PMS.Shared.Services;

namespace PMS.API.Controllers;

/// <summary>The customer's own parking sessions: running fee and prepaying in the app (FR-PAY-07, FR-CUS-02).</summary>
[Route("api/parking-sessions")]
[Authorize(Roles = "User")]
public class ParkingSessionsController(IGateService gateService, IClaimService claimService) : ApiController
{
    [HttpGet("mine")]
    public async Task<IActionResult> GetMineAsync() =>
        Ok(ApiResult<IReadOnlyList<SessionResponseModel>>.Success(await gateService.GetMySessionsAsync(claimService.GetUserId())));

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
