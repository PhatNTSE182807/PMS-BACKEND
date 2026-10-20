using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Operations;
using PMS.Application.Services;
using PMS.Shared.Services;

namespace PMS.API.Controllers;

/// <summary>The signed-in customer's slot reservations (SRS FR-RSV-02..05, UC-04).</summary>
[Route("api/reservations")]
[Authorize(Roles = "User")]
public class ReservationsController(
    IReservationService reservationService,
    IPaymentService paymentService,
    IClaimService claimService) : ApiController
{
    /// <summary>Holds the slot (first holder wins: 409 when someone else got it) and starts the deposit deadline.</summary>
    [HttpPost]
    public async Task<IActionResult> CreateAsync([FromBody] CreateReservationModel model)
    {
        var created = await reservationService.CreateAsync(claimService.GetUserId(), model);
        return StatusCode(StatusCodes.Status201Created, ApiResult<ReservationResponseModel>.Success(created));
    }

    [HttpGet("mine")]
    public async Task<IActionResult> GetMineAsync([FromQuery] bool activeOnly = false)
    {
        return Ok(ApiResult<IReadOnlyList<ReservationResponseModel>>.Success(
            await reservationService.GetMineAsync(claimService.GetUserId(), activeOnly)));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetAsync(long id)
    {
        return Ok(ApiResult<ReservationResponseModel>.Success(await reservationService.GetAsync(claimService.GetUserId(), id)));
    }

    /// <summary>Change the arrival time and/or the slot before the change deadline (FR-RSV-03).</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> UpdateAsync(long id, [FromBody] UpdateReservationModel model)
    {
        return Ok(ApiResult<ReservationResponseModel>.Success(await reservationService.UpdateAsync(claimService.GetUserId(), id, model)));
    }

    /// <summary>Cancel; the deposit is refunded per BR-04.</summary>
    [HttpPost("{id:long}/cancel")]
    public async Task<IActionResult> CancelAsync(long id, [FromBody] CancelReservationModel model)
    {
        return Ok(ApiResult<ReservationResponseModel>.Success(await reservationService.CancelAsync(claimService.GetUserId(), id, model?.Reason)));
    }

    /// <summary>Signed QR to check in during the arrival window (FR-RSV-04).</summary>
    [HttpGet("{id:long}/qr")]
    public async Task<IActionResult> GetQrAsync(long id)
    {
        return Ok(ApiResult<ReservationQrModel>.Success(await reservationService.GetQrAsync(claimService.GetUserId(), id)));
    }

    /// <summary>Pay the deposit with VNPay or MoMo; redirect the browser to the returned payUrl.</summary>
    [HttpPost("{id:long}/payments")]
    public async Task<IActionResult> PayDepositAsync(long id, [FromBody] CreatePaymentModel model)
    {
        return Ok(ApiResult<PaymentResponseModel>.Success(
            await paymentService.CreateDepositPaymentAsync(id, claimService.GetUserId(), model, PaymentsController.RequestContext(HttpContext))));
    }
}
