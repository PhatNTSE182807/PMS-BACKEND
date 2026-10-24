using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Operations;
using PMS.Application.Services;

namespace PMS.API.Controllers;

/// <summary>
/// Gateway callbacks and payment status. The return endpoints are what the frontend return page calls
/// with the query string the gateway sent the browser back with; the IPN endpoints are called by the
/// gateway itself. Both check the signature and are idempotent, so whichever arrives first applies the
/// result (IPN cannot reach localhost during development).
/// </summary>
[Route("api/payments")]
public class PaymentsController(IPaymentService paymentService, ILogger<PaymentsController> logger) : ApiController
{
    /// <summary>The caller's IP and this API's base URL, for the gateways.</summary>
    public static PaymentRequestContext RequestContext(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress;
        var clientIp = ip == null ? "127.0.0.1" : ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
        if (clientIp == "::1") clientIp = "127.0.0.1";
        return new PaymentRequestContext(clientIp, $"{http.Request.Scheme}://{http.Request.Host}");
    }

    [HttpGet("{id:long}")]
    [Authorize]
    public async Task<IActionResult> GetAsync(long id)
    {
        return Ok(ApiResult<PaymentResponseModel>.Success(await paymentService.GetAsync(id)));
    }

    /// <summary>FR-PAY-08: ask the gateway about a pending payment (no callback after BR-11's wait).</summary>
    [HttpPost("{id:long}/check")]
    [Authorize]
    public async Task<IActionResult> CheckAsync(long id)
    {
        return Ok(ApiResult<PaymentCallbackResultModel>.Success(await paymentService.CheckStatusAsync(id, RequestContext(HttpContext).ClientIp)));
    }

    /// <summary>Whether this server allows simulated payments (development / testing only), so the UI can show the test button.</summary>
    [HttpGet("simulation")]
    [AllowAnonymous]
    public IActionResult GetSimulation()
    {
        return Ok(ApiResult<PaymentSimulationModel>.Success(new PaymentSimulationModel { Enabled = paymentService.SimulationEnabled }));
    }

    /// <summary>
    /// Development / testing only: completes the caller's pending VNPay or MoMo payment as if the gateway
    /// confirmed it (deposit confirmed, wallet credited…). Refused when Payments:AllowSimulatedPayments is off.
    /// </summary>
    [HttpPost("{id:long}/simulate-success")]
    [Authorize]
    public async Task<IActionResult> SimulateSuccessAsync(long id)
    {
        return Ok(ApiResult<PaymentCallbackResultModel>.Success(await paymentService.SimulateSuccessAsync(id)));
    }

    /// <summary>VNPay return URL parameters (vnp_*), forwarded by the frontend return page.</summary>
    [HttpGet("vnpay/return")]
    [AllowAnonymous]
    public async Task<IActionResult> VnPayReturnAsync()
    {
        var result = await paymentService.HandleCallbackAsync(PaymentProviders.VnPay, QueryFields());
        return Ok(ApiResult<PaymentCallbackResultModel>.Success(result));
    }

    /// <summary>VNPay IPN: answers with RspCode as the VNPay docs require.</summary>
    [HttpGet("vnpay/ipn")]
    [AllowAnonymous]
    public async Task<IActionResult> VnPayIpnAsync()
    {
        try
        {
            var result = await paymentService.HandleCallbackAsync(PaymentProviders.VnPay, QueryFields());
            return VnPayAnswer(result.Outcome switch
            {
                "Applied" or "Refunded" or "Failed" => ("00", "Confirm Success"),
                "AlreadyProcessed" => ("02", "Order already confirmed"),
                "NotFound" => ("01", "Order not found"),
                "InvalidAmount" => ("04", "Invalid amount"),
                "InvalidSignature" => ("97", "Invalid signature"),
                _ => ("99", "Unknown error")
            });
        }
        catch (Exception e)
        {
            logger.LogError(e, "VNPay IPN failed.");
            return VnPayAnswer(("99", "Unknown error"));
        }
    }

    /// <summary>VNPay reads "RspCode" / "Message" exactly; a dictionary keeps the names out of camelCase.</summary>
    private OkObjectResult VnPayAnswer((string Code, string Message) answer) =>
        Ok(new Dictionary<string, string> { ["RspCode"] = answer.Code, ["Message"] = answer.Message });

    /// <summary>MoMo redirect URL parameters, forwarded by the frontend return page.</summary>
    [HttpGet("momo/return")]
    [AllowAnonymous]
    public async Task<IActionResult> MoMoReturnAsync()
    {
        var result = await paymentService.HandleCallbackAsync(PaymentProviders.MoMo, QueryFields());
        return Ok(ApiResult<PaymentCallbackResultModel>.Success(result));
    }

    /// <summary>MoMo IPN (JSON body); MoMo expects HTTP 204.</summary>
    [HttpPost("momo/ipn")]
    [AllowAnonymous]
    public async Task<IActionResult> MoMoIpnAsync([FromBody] Dictionary<string, JsonElement> body)
    {
        var fields = (body ?? []).ToDictionary(
            p => p.Key,
            p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText());
        try
        {
            var result = await paymentService.HandleCallbackAsync(PaymentProviders.MoMo, fields);
            if (result.Outcome == "InvalidSignature")
                return BadRequest();
        }
        catch (Exception e)
        {
            logger.LogError(e, "MoMo IPN failed.");
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        return NoContent();
    }

    private Dictionary<string, string> QueryFields() =>
        Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString());
}
