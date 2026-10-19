namespace PMS.Application.Services.Payments;

/// <summary>
/// One online payment provider (SRS E1: VNPay, MoMo). Implementations only talk to the provider and
/// check signatures; what a payment means for a reservation or session is PaymentService's job.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>"VNPay" or "MoMo" (see PaymentProviders).</summary>
    string Provider { get; }

    /// <summary>Merchant keys are present.</summary>
    bool IsConfigured { get; }

    /// <summary>Creates the hosted payment page; the customer is redirected to <see cref="GatewayCheckout.PayUrl"/>.</summary>
    Task<GatewayCheckout> CreateCheckoutAsync(GatewayCheckoutRequest request, CancellationToken cancellationToken = default);

    /// <summary>Checks the signature of a return/IPN callback and reads its result.</summary>
    GatewayCallback ParseCallback(IReadOnlyDictionary<string, string> fields);

    /// <summary>Asks the provider for the transaction status (FR-PAY-08 "kiểm tra trạng thái giao dịch với E1").</summary>
    Task<GatewayStatus> QueryAsync(GatewayQueryRequest request, CancellationToken cancellationToken = default);

    /// <summary>Returns money to the original source (FR-PAY-10).</summary>
    Task<GatewayRefundResult> RefundAsync(GatewayRefundRequest request, CancellationToken cancellationToken = default);
}

/// <param name="PaymentCode">Our unique code, sent as vnp_TxnRef / MoMo orderId.</param>
/// <param name="Amount">VND, whole number.</param>
public sealed record GatewayCheckoutRequest(
    string PaymentCode,
    decimal Amount,
    string OrderInfo,
    string ReturnUrl,
    string IpnUrl,
    string ClientIp,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc);

public sealed record GatewayCheckout(string PayUrl, string RawResponse = null);

/// <param name="SignatureValid">False means the callback must be ignored (VNPay RspCode 97).</param>
/// <param name="Success">The provider says the money was taken.</param>
public sealed record GatewayCallback(
    bool SignatureValid,
    string PaymentCode,
    decimal Amount,
    bool Success,
    string TransactionNo,
    string ResponseCode,
    string Message);

/// <param name="PaymentCode">Our code of the payment to look up.</param>
/// <param name="CreatedAtUtc">When the payment was created (VNPay needs the original date).</param>
public sealed record GatewayQueryRequest(string PaymentCode, DateTime CreatedAtUtc, string ClientIp);

public enum GatewayPaymentState
{
    Pending,
    Succeeded,
    Failed,
    /// <summary>The provider could not be asked, or its answer could not be verified.</summary>
    Unknown
}

public sealed record GatewayStatus(GatewayPaymentState State, string TransactionNo, decimal? Amount, string Message);

/// <param name="RefundCode">Our new code for the refund (MoMo needs a fresh orderId).</param>
/// <param name="OriginalPaymentCode">The paid payment's code.</param>
/// <param name="IsFullRefund">VNPay transaction type 02 (full) or 03 (partial).</param>
public sealed record GatewayRefundRequest(
    string RefundCode,
    string OriginalPaymentCode,
    string OriginalTransactionNo,
    DateTime OriginalCreatedAtUtc,
    decimal Amount,
    bool IsFullRefund,
    string Reason,
    string RequestedBy,
    string ClientIp);

public sealed record GatewayRefundResult(bool Accepted, string TransactionNo, string Message);
