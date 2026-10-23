using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services;

/// <summary>Where a payment request comes from: the caller's IP (VNPay wants it) and this API's public base URL (IPN).</summary>
public sealed record PaymentRequestContext(string ClientIp, string ApiBaseUrl);

/// <summary>
/// Deposits and parking fees paid online (VNPay / MoMo) or in cash, and the automatic refunds of SRS
/// FR-PAY-08..10 and FR-RSV-08. Callbacks are idempotent: the same IPN applied twice changes nothing.
/// </summary>
public interface IPaymentService
{
    /// <summary>Pays a reservation's deposit (FR-RSV-02). Reuses an open link for the same provider.</summary>
    Task<PaymentResponseModel> CreateDepositPaymentAsync(long reservationId, string userId, CreatePaymentModel model, PaymentRequestContext context);

    /// <summary>Online payment of what a session owes now (app prepay or a lane/kiosk QR).</summary>
    Task<PaymentResponseModel> CreateSessionPaymentAsync(ParkingSession session, decimal amountDue, string provider, string channel,
        string returnUrl, string customerUserId, string staffUserId, PaymentRequestContext context);

    /// <summary>Cash at the lane or a counter (FR-PAY-01): settles the session at once.</summary>
    Task<PaymentResponseModel> RecordCashAsync(ParkingSession session, decimal amountDue, decimal received, bool prepaid, string staffUserId);

    /// <summary>Return URL or IPN of a gateway: checks the signature, then applies the result once.</summary>
    Task<PaymentCallbackResultModel> HandleCallbackAsync(string provider, IReadOnlyDictionary<string, string> fields);

    /// <summary>FR-PAY-08: asks the gateway for the status of a pending payment and applies the answer.</summary>
    Task<PaymentCallbackResultModel> CheckStatusAsync(long paymentId, string clientIp);

    /// <summary>A payment of the caller (customers see their own; Staff/Managers those of their lots).</summary>
    Task<PaymentResponseModel> GetAsync(long paymentId);

    /// <summary>Open payment (Pending, not expired) of a reservation, if any.</summary>
    Task<PaymentResponseModel> GetPendingForReservationAsync(long reservationId);

    /// <summary>Open online payment (Pending, not expired, with a pay link) of a session's parking fee, if any.</summary>
    Task<PaymentResponseModel> GetPendingForSessionAsync(long sessionId);

    /// <summary>Deposit money held for a reservation: paid minus refunded.</summary>
    Task<decimal> DepositHeldAsync(long reservationId);

    /// <summary>Refunds part or all of a reservation's deposit to its source (FR-PAY-10). Returns the amount refunded or queued.</summary>
    Task<decimal> RefundDepositAsync(Reservation reservation, decimal amount, string reason, string requestedBy);

    /// <summary>Job: expires stale links and asks the gateway about payments waiting longer than BR-11.</summary>
    Task<int> CheckPendingPaymentsAsync();

    /// <summary>Job: retries refunds the gateway did not accept yet.</summary>
    Task<int> RetryPendingRefundsAsync();

    /// <summary>Starts an online top-up of the user's XXIV Wallet (VNPay / MoMo).</summary>
    Task<PaymentResponseModel> CreateWalletTopUpPaymentAsync(string userId, TopUpWalletModel model, PaymentRequestContext context);

    /// <summary>Gets the user's current wallet balance, hold amount, and recent transactions.</summary>
    Task<WalletResponseModel> GetWalletAsync(string userId);

    /// <summary>Withdraws funds from the user's wallet to a bank account or MoMo.</summary>
    Task<WalletResponseModel> WithdrawWalletAsync(string userId, WithdrawWalletModel model);
}
