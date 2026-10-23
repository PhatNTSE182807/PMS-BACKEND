using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Operations;
using PMS.Application.Services;
using PMS.Shared.Services;

namespace PMS.API.Controllers;

/// <summary>XXIV User Wallet: balance telemetry, VNPay/MoMo top-ups, and withdrawals.</summary>
[Route("api/wallet")]
[Authorize]
public class WalletController(
    IPaymentService paymentService,
    IClaimService claimService) : ApiController
{
    /// <summary>Gets the current user's wallet balance, pending exit hold, and recent transactions.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAsync()
    {
        var wallet = await paymentService.GetWalletAsync(claimService.GetUserId());
        return Ok(ApiResult<WalletResponseModel>.Success(wallet));
    }

    /// <summary>Starts a VNPay or MoMo online payment to top up the user's XXIV Wallet.</summary>
    [HttpPost("topup")]
    public async Task<IActionResult> TopUpAsync([FromBody] TopUpWalletModel model)
    {
        var payment = await paymentService.CreateWalletTopUpPaymentAsync(
            claimService.GetUserId(),
            model,
            PaymentsController.RequestContext(HttpContext));
        return Ok(ApiResult<PaymentResponseModel>.Success(payment));
    }

    /// <summary>Withdraws money from the user's wallet to a bank account or MoMo.</summary>
    [HttpPost("withdraw")]
    public async Task<IActionResult> WithdrawAsync([FromBody] WithdrawWalletModel model)
    {
        var wallet = await paymentService.WithdrawWalletAsync(claimService.GetUserId(), model);
        return Ok(ApiResult<WalletResponseModel>.Success(wallet));
    }
}
