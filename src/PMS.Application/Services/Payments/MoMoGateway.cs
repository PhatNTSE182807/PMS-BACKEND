using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PMS.Application.Models.Operations;

namespace PMS.Application.Services.Payments;

/// <summary>
/// MoMo AIO v2 "captureWallet" (sandbox host test-payment.momo.vn by default). Every request and
/// callback is signed with HMAC-SHA256 (hex) over "key=value" pairs in the documented order.
/// Docs: https://developers.momo.vn/v3/docs/payment/api/wallet/onetime
/// </summary>
public class MoMoGateway(
    PaymentGatewayOptions options,
    IHttpClientFactory httpClientFactory,
    ILogger<MoMoGateway> logger = null) : IPaymentGateway
{
    public const string HttpClientName = "momo";

    private MoMoOptions Settings => options.MoMo;

    public string Provider => PaymentProviders.MoMo;

    public bool IsConfigured => Settings.IsConfigured;

    public async Task<GatewayCheckout> CreateCheckoutAsync(GatewayCheckoutRequest request, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var amount = ToAmount(request.Amount);
        var requestId = request.PaymentCode;
        const string extraData = "";
        var raw = CreateSignatureData(Settings.AccessKey, amount, extraData, request.IpnUrl, request.PaymentCode, request.OrderInfo,
            Settings.PartnerCode, request.ReturnUrl, requestId, Settings.RequestType);

        var body = new Dictionary<string, object>
        {
            ["partnerCode"] = Settings.PartnerCode,
            ["requestId"] = requestId,
            ["amount"] = long.Parse(amount, CultureInfo.InvariantCulture),
            ["orderId"] = request.PaymentCode,
            ["orderInfo"] = request.OrderInfo,
            ["redirectUrl"] = request.ReturnUrl,
            ["ipnUrl"] = request.IpnUrl,
            ["requestType"] = Settings.RequestType,
            ["extraData"] = extraData,
            ["autoCapture"] = true,
            ["lang"] = Settings.Lang,
            // MoMo keeps the link open this long (minutes)
            ["orderExpireTime"] = Math.Max(1, (int)Math.Round((request.ExpiresAtUtc - request.CreatedAtUtc).TotalMinutes)),
            ["signature"] = Sign(Settings.SecretKey, raw)
        };

        var response = await PostAsync("/v2/gateway/api/create", body, cancellationToken)
            ?? throw new InvalidOperationException("MoMo did not answer the payment request.");

        var resultCode = Field(response, "resultCode");
        var payUrl = Field(response, "payUrl");
        if (resultCode != "0" || string.IsNullOrWhiteSpace(payUrl))
            throw new InvalidOperationException($"MoMo refused the payment: {Field(response, "message")} ({resultCode}).");

        return new GatewayCheckout(payUrl, JsonSerializer.Serialize(response));
    }

    public GatewayCallback ParseCallback(IReadOnlyDictionary<string, string> fields)
    {
        EnsureConfigured();

        string F(string key) => fields.TryGetValue(key, out var v) ? v ?? string.Empty : string.Empty;
        var valid = VerifyCallback(Settings.AccessKey, Settings.SecretKey, fields);
        var amount = decimal.TryParse(F("amount"), NumberStyles.Number, CultureInfo.InvariantCulture, out var a) ? a : 0;

        // 0 = paid; 9000 = authorised (only without autoCapture); anything else failed or was cancelled
        var resultCode = F("resultCode");
        return new GatewayCallback(valid, F("orderId"), amount, resultCode is "0" or "9000", F("transId"), resultCode, F("message"));
    }

    public async Task<GatewayStatus> QueryAsync(GatewayQueryRequest request, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var requestId = Guid.NewGuid().ToString("N");
        var raw = $"accessKey={Settings.AccessKey}&orderId={request.PaymentCode}&partnerCode={Settings.PartnerCode}&requestId={requestId}";
        var response = await PostAsync("/v2/gateway/api/query", new Dictionary<string, object>
        {
            ["partnerCode"] = Settings.PartnerCode,
            ["requestId"] = requestId,
            ["orderId"] = request.PaymentCode,
            ["lang"] = Settings.Lang,
            ["signature"] = Sign(Settings.SecretKey, raw)
        }, cancellationToken);

        if (response == null)
            return new GatewayStatus(GatewayPaymentState.Unknown, null, null, "MoMo did not answer.");

        decimal? amount = decimal.TryParse(Field(response, "amount"), NumberStyles.Number, CultureInfo.InvariantCulture, out var a) ? a : null;
        var state = Field(response, "resultCode") switch
        {
            "0" or "9000" => GatewayPaymentState.Succeeded,
            // 1000: waiting for the customer; 7000/7002: being processed
            "1000" or "7000" or "7002" => GatewayPaymentState.Pending,
            _ => GatewayPaymentState.Failed
        };
        return new GatewayStatus(state, Field(response, "transId"), amount, Field(response, "message"));
    }

    public async Task<GatewayRefundResult> RefundAsync(GatewayRefundRequest request, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var amount = ToAmount(request.Amount);
        var description = string.IsNullOrWhiteSpace(request.Reason) ? $"Refund {request.OriginalPaymentCode}" : request.Reason;
        var requestId = request.RefundCode;
        var raw = $"accessKey={Settings.AccessKey}&amount={amount}&description={description}&orderId={request.RefundCode}" +
                  $"&partnerCode={Settings.PartnerCode}&requestId={requestId}&transId={request.OriginalTransactionNo}";

        var response = await PostAsync("/v2/gateway/api/refund", new Dictionary<string, object>
        {
            ["partnerCode"] = Settings.PartnerCode,
            ["orderId"] = request.RefundCode,
            ["requestId"] = requestId,
            ["amount"] = long.Parse(amount, CultureInfo.InvariantCulture),
            ["transId"] = long.TryParse(request.OriginalTransactionNo, out var transId) ? transId : 0,
            ["lang"] = Settings.Lang,
            ["description"] = description,
            ["signature"] = Sign(Settings.SecretKey, raw)
        }, cancellationToken);

        if (response == null)
            return new GatewayRefundResult(false, null, "MoMo did not answer.");

        return new GatewayRefundResult(Field(response, "resultCode") == "0", Field(response, "transId"), Field(response, "message"));
    }

    /// <summary>Signed string of a create request, in MoMo's documented key order.</summary>
    public static string CreateSignatureData(string accessKey, string amount, string extraData, string ipnUrl, string orderId,
        string orderInfo, string partnerCode, string redirectUrl, string requestId, string requestType) =>
        $"accessKey={accessKey}&amount={amount}&extraData={extraData}&ipnUrl={ipnUrl}&orderId={orderId}&orderInfo={orderInfo}" +
        $"&partnerCode={partnerCode}&redirectUrl={redirectUrl}&requestId={requestId}&requestType={requestType}";

    /// <summary>Signed string of a redirect / IPN callback, in MoMo's documented key order.</summary>
    public static string CallbackSignatureData(string accessKey, IReadOnlyDictionary<string, string> fields)
    {
        string F(string key) => fields.TryGetValue(key, out var v) ? v ?? string.Empty : string.Empty;
        return $"accessKey={accessKey}&amount={F("amount")}&extraData={F("extraData")}&message={F("message")}&orderId={F("orderId")}" +
               $"&orderInfo={F("orderInfo")}&orderType={F("orderType")}&partnerCode={F("partnerCode")}&payType={F("payType")}" +
               $"&requestId={F("requestId")}&responseTime={F("responseTime")}&resultCode={F("resultCode")}&transId={F("transId")}";
    }

    public static bool VerifyCallback(string accessKey, string secretKey, IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("signature", out var signature) || string.IsNullOrWhiteSpace(signature))
            return false;

        var expected = Sign(secretKey, CallbackSignatureData(accessKey, fields));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant()));
    }

    /// <summary>HMAC-SHA256, lower-case hex.</summary>
    public static string Sign(string secretKey, string data) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretKey), Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

    private static string ToAmount(decimal amount) =>
        ((long)decimal.Round(amount, 0)).ToString(CultureInfo.InvariantCulture);

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new InvalidOperationException("MoMo is not configured (Payments:MoMo:PartnerCode / AccessKey / SecretKey).");
    }

    private static string Field(Dictionary<string, JsonElement> json, string key) =>
        json.TryGetValue(key, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : string.Empty;

    private async Task<Dictionary<string, JsonElement>> PostAsync(string path, Dictionary<string, object> body, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync($"{Settings.Endpoint.TrimEnd('/')}{path}", body, cancellationToken);
            return await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>(cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            logger?.LogWarning(e, "MoMo API call failed ({Path}).", path);
            return null;
        }
    }
}
