using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;

namespace PMS.Application.Services.Payments;

/// <summary>
/// VNPay payment gateway v2.1.0 (sandbox by default). Payment URL and callbacks are signed with
/// HMAC-SHA512 over the URL-encoded, key-sorted vnp_ fields; querydr and refund over "|"-joined values.
/// Docs: https://sandbox.vnpayment.vn/apis/docs/thanh-toan-pay/pay.html and …/truy-van-hoan-tien/querydr&amp;refund.html
/// </summary>
public class VnPayGateway(
    PaymentGatewayOptions options,
    IHttpClientFactory httpClientFactory,
    ILogger<VnPayGateway> logger = null) : IPaymentGateway
{
    public const string HttpClientName = "vnpay";

    private static readonly TimeZoneInfo VietnamTime = ParkingText.FindTimeZone("SE Asia Standard Time");

    private VnPayOptions Settings => options.VnPay;

    public string Provider => PaymentProviders.VnPay;

    public bool IsConfigured => Settings.IsConfigured;

    public Task<GatewayCheckout> CreateCheckoutAsync(GatewayCheckoutRequest request, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["vnp_Version"] = Settings.Version,
            ["vnp_Command"] = "pay",
            ["vnp_TmnCode"] = Settings.TmnCode,
            // VNPay amounts are in 1/100 VND
            ["vnp_Amount"] = ToVnPayAmount(request.Amount),
            ["vnp_CreateDate"] = FormatDate(request.CreatedAtUtc),
            ["vnp_ExpireDate"] = FormatDate(request.ExpiresAtUtc),
            ["vnp_CurrCode"] = "VND",
            ["vnp_IpAddr"] = string.IsNullOrWhiteSpace(request.ClientIp) ? "127.0.0.1" : request.ClientIp,
            ["vnp_Locale"] = Settings.Locale,
            ["vnp_OrderInfo"] = request.OrderInfo,
            ["vnp_OrderType"] = "other",
            ["vnp_ReturnUrl"] = request.ReturnUrl,
            ["vnp_TxnRef"] = request.PaymentCode
        };

        var query = BuildQuery(fields);
        var url = $"{Settings.PaymentUrl}?{query}&vnp_SecureHash={Sign(Settings.HashSecret, query)}";
        return Task.FromResult(new GatewayCheckout(url));
    }

    public GatewayCallback ParseCallback(IReadOnlyDictionary<string, string> fields)
    {
        EnsureConfigured();

        fields.TryGetValue("vnp_TxnRef", out var code);
        fields.TryGetValue("vnp_ResponseCode", out var responseCode);
        fields.TryGetValue("vnp_TransactionStatus", out var transactionStatus);
        fields.TryGetValue("vnp_TransactionNo", out var transactionNo);
        fields.TryGetValue("vnp_Amount", out var amountText);
        var amount = long.TryParse(amountText, out var raw) ? raw / 100m : 0;

        var valid = VerifySignature(Settings.HashSecret, fields);
        var success = responseCode == "00" && (transactionStatus == null || transactionStatus == "00");
        return new GatewayCallback(valid, code, amount, success, transactionNo, responseCode, ResponseMessage(responseCode));
    }

    public async Task<GatewayStatus> QueryAsync(GatewayQueryRequest request, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var requestId = Guid.NewGuid().ToString("N");
        var createDate = FormatDate(DateTime.UtcNow);
        var transactionDate = FormatDate(request.CreatedAtUtc);
        var ip = string.IsNullOrWhiteSpace(request.ClientIp) ? "127.0.0.1" : request.ClientIp;
        var orderInfo = $"Query {request.PaymentCode}";
        var data = string.Join('|', requestId, Settings.Version, "querydr", Settings.TmnCode, request.PaymentCode, transactionDate, createDate, ip, orderInfo);

        var body = new Dictionary<string, string>
        {
            ["vnp_RequestId"] = requestId,
            ["vnp_Version"] = Settings.Version,
            ["vnp_Command"] = "querydr",
            ["vnp_TmnCode"] = Settings.TmnCode,
            ["vnp_TxnRef"] = request.PaymentCode,
            ["vnp_OrderInfo"] = orderInfo,
            ["vnp_TransactionDate"] = transactionDate,
            ["vnp_CreateDate"] = createDate,
            ["vnp_IpAddr"] = ip,
            ["vnp_SecureHash"] = Sign(Settings.HashSecret, data)
        };

        var response = await PostAsync(body, cancellationToken);
        if (response == null)
            return new GatewayStatus(GatewayPaymentState.Unknown, null, null, "VNPay did not answer.");

        string F(string key) => response.TryGetValue(key, out var v) ? v : string.Empty;
        var expected = string.Join('|', F("vnp_ResponseId"), F("vnp_Command"), F("vnp_ResponseCode"), F("vnp_Message"), F("vnp_TmnCode"),
            F("vnp_TxnRef"), F("vnp_Amount"), F("vnp_BankCode"), F("vnp_PayDate"), F("vnp_TransactionNo"), F("vnp_TransactionType"),
            F("vnp_TransactionStatus"), F("vnp_OrderInfo"), F("vnp_PromotionCode"), F("vnp_PromotionAmount"));
        if (!FixedTimeEqualsHex(Sign(Settings.HashSecret, expected), F("vnp_SecureHash")))
            return new GatewayStatus(GatewayPaymentState.Unknown, null, null, "VNPay answer has an invalid signature.");

        if (F("vnp_ResponseCode") != "00")
            return new GatewayStatus(GatewayPaymentState.Unknown, null, null, F("vnp_Message"));

        decimal? amount = long.TryParse(F("vnp_Amount"), out var raw) ? raw / 100m : null;
        var state = F("vnp_TransactionStatus") switch
        {
            "00" => GatewayPaymentState.Succeeded,
            "01" => GatewayPaymentState.Pending,
            _ => GatewayPaymentState.Failed
        };
        return new GatewayStatus(state, F("vnp_TransactionNo"), amount, F("vnp_Message"));
    }

    public async Task<GatewayRefundResult> RefundAsync(GatewayRefundRequest request, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var requestId = Guid.NewGuid().ToString("N");
        var createDate = FormatDate(DateTime.UtcNow);
        var transactionDate = FormatDate(request.OriginalCreatedAtUtc);
        var ip = string.IsNullOrWhiteSpace(request.ClientIp) ? "127.0.0.1" : request.ClientIp;
        var type = request.IsFullRefund ? "02" : "03";
        var amount = ToVnPayAmount(request.Amount);
        var createBy = string.IsNullOrWhiteSpace(request.RequestedBy) ? "system" : request.RequestedBy;
        var orderInfo = string.IsNullOrWhiteSpace(request.Reason) ? $"Refund {request.OriginalPaymentCode}" : request.Reason;
        var transactionNo = request.OriginalTransactionNo ?? string.Empty;
        var data = string.Join('|', requestId, Settings.Version, "refund", Settings.TmnCode, type, request.OriginalPaymentCode, amount,
            transactionNo, transactionDate, createBy, createDate, ip, orderInfo);

        var body = new Dictionary<string, string>
        {
            ["vnp_RequestId"] = requestId,
            ["vnp_Version"] = Settings.Version,
            ["vnp_Command"] = "refund",
            ["vnp_TmnCode"] = Settings.TmnCode,
            ["vnp_TransactionType"] = type,
            ["vnp_TxnRef"] = request.OriginalPaymentCode,
            ["vnp_Amount"] = amount,
            ["vnp_OrderInfo"] = orderInfo,
            ["vnp_TransactionNo"] = transactionNo,
            ["vnp_TransactionDate"] = transactionDate,
            ["vnp_CreateBy"] = createBy,
            ["vnp_CreateDate"] = createDate,
            ["vnp_IpAddr"] = ip,
            ["vnp_SecureHash"] = Sign(Settings.HashSecret, data)
        };

        var response = await PostAsync(body, cancellationToken);
        if (response == null)
            return new GatewayRefundResult(false, null, "VNPay did not answer.");

        response.TryGetValue("vnp_ResponseCode", out var code);
        response.TryGetValue("vnp_Message", out var message);
        response.TryGetValue("vnp_TransactionNo", out var refundNo);
        return new GatewayRefundResult(code == "00", refundNo, message);
    }

    /// <summary>
    /// The string VNPay signs: vnp_ fields with a value, sorted by key (ordinal), each key and value
    /// URL-encoded (spaces as '+'), joined with '&amp;'.
    /// </summary>
    public static string BuildQuery(IEnumerable<KeyValuePair<string, string>> fields) =>
        string.Join('&', fields
            .Where(f => !string.IsNullOrEmpty(f.Value))
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => $"{WebUtility.UrlEncode(f.Key)}={WebUtility.UrlEncode(f.Value)}"));

    /// <summary>HMAC-SHA512, lower-case hex.</summary>
    public static string Sign(string secret, string data) =>
        Convert.ToHexString(HMACSHA512.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

    /// <summary>Checks vnp_SecureHash of a return / IPN query (all vnp_ fields except the hash ones).</summary>
    public static bool VerifySignature(string secret, IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("vnp_SecureHash", out var hash) || string.IsNullOrWhiteSpace(hash))
            return false;

        var signed = fields.Where(f => f.Key.StartsWith("vnp_", StringComparison.Ordinal)
                                       && f.Key != "vnp_SecureHash" && f.Key != "vnp_SecureHashType");
        return FixedTimeEqualsHex(Sign(secret, BuildQuery(signed)), hash);
    }

    /// <summary>yyyyMMddHHmmss in Vietnam time (GMT+7), as VNPay expects.</summary>
    public static string FormatDate(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), VietnamTime).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    private static string ToVnPayAmount(decimal amount) =>
        ((long)decimal.Round(amount * 100, 0)).ToString(CultureInfo.InvariantCulture);

    private static bool FixedTimeEqualsHex(string expected, string actual) =>
        !string.IsNullOrEmpty(actual)
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual.Trim().ToLowerInvariant()));

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new InvalidOperationException("VNPay is not configured (Payments:VnPay:TmnCode / HashSecret).");
    }

    private async Task<Dictionary<string, string>> PostAsync(Dictionary<string, string> body, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync(Settings.ApiUrl, body, cancellationToken);
            var json = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>(cancellationToken);
            return json?.ToDictionary(p => p.Key, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            logger?.LogWarning(e, "VNPay API call failed.");
            return null;
        }
    }

    private static string ResponseMessage(string code) => code switch
    {
        "00" => "Transaction successful",
        "07" => "Amount debited, but the transaction is flagged as suspicious",
        "09" => "The card/account is not registered for Internet Banking",
        "10" => "Card/account verification failed more than 3 times",
        "11" => "The payment window has expired",
        "12" => "The card/account is locked",
        "13" => "Wrong transaction OTP",
        "24" => "The customer cancelled the transaction",
        "51" => "Insufficient balance",
        "65" => "The daily transaction limit was exceeded",
        "75" => "The paying bank is under maintenance",
        "79" => "Wrong payment password too many times",
        _ => "Transaction failed"
    };
}
