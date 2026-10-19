namespace PMS.Application.Services.Payments;

/// <summary>
/// "Payments" configuration section. Merchant keys are never committed: set them with user-secrets
/// (dotnet user-secrets set "Payments:VnPay:HashSecret" "...") or environment variables
/// (Payments__VnPay__HashSecret). A gateway without keys reports itself as not configured.
/// </summary>
public class PaymentGatewayOptions
{
    public const string Section = "Payments";

    /// <summary>
    /// Frontend page the gateway sends the customer back to; the provider is appended as a path segment
    /// (…/payment/return/vnpay, …/payment/return/momo).
    /// </summary>
    public string FrontendReturnUrl { get; set; } = "http://localhost:5173/payment/return";

    /// <summary>Origins a client may pass as its own return URL (anything else falls back to <see cref="FrontendReturnUrl"/>).</summary>
    public string[] AllowedReturnOrigins { get; set; } = ["http://localhost:5173", "http://localhost:4173"];

    /// <summary>
    /// Public base URL of this API for IPN callbacks (e.g. an ngrok/dev-tunnel URL in development).
    /// When empty the request's own scheme and host are used, which the gateways cannot reach from localhost.
    /// </summary>
    public string PublicApiBaseUrl { get; set; }

    public VnPayOptions VnPay { get; set; } = new();

    public MoMoOptions MoMo { get; set; } = new();
}

/// <summary>VNPay merchant settings (https://sandbox.vnpayment.vn/apis/docs/thanh-toan-pay/pay.html).</summary>
public class VnPayOptions
{
    public string TmnCode { get; set; }

    public string HashSecret { get; set; }

    public string PaymentUrl { get; set; } = "https://sandbox.vnpayment.vn/paymentv2/vpcpay.html";

    /// <summary>querydr / refund endpoint.</summary>
    public string ApiUrl { get; set; } = "https://sandbox.vnpayment.vn/merchant_webapi/api/transaction";

    public string Version { get; set; } = "2.1.0";

    public string Locale { get; set; } = "vn";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(TmnCode) && !string.IsNullOrWhiteSpace(HashSecret);
}

/// <summary>MoMo AIO v2 settings (https://developers.momo.vn/v3/docs/payment/api/wallet/onetime).</summary>
public class MoMoOptions
{
    public string PartnerCode { get; set; }

    public string AccessKey { get; set; }

    public string SecretKey { get; set; }

    public string Endpoint { get; set; } = "https://test-payment.momo.vn";

    public string RequestType { get; set; } = "captureWallet";

    public string Lang { get; set; } = "vi";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PartnerCode) && !string.IsNullOrWhiteSpace(AccessKey) && !string.IsNullOrWhiteSpace(SecretKey);
}
