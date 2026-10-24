namespace PMS.Application.Models.Operations;

/// <summary>Start an online payment with VNPay or MoMo.</summary>
public class CreatePaymentModel
{
    /// <summary>"VNPay" or "MoMo".</summary>
    public string Provider { get; set; }

    /// <summary>Optional frontend return page; only origins in Payments:AllowedReturnOrigins are accepted.</summary>
    public string ReturnUrl { get; set; }
}

/// <summary>Cash taken at the exit lane or the counter (FR-PAY-01).</summary>
public class CashPaymentModel
{
    /// <summary>Money received; must cover the amount due (change is given back, only the due amount is recorded).</summary>
    public decimal Amount { get; set; }

    /// <summary>True when paid at a counter/kiosk before driving to the exit: opens an Exit Window (FR-PAY-07).</summary>
    public bool Prepaid { get; set; }
}

/// <summary>Online payment started by the Staff at a lane (dynamic QR for a Guest, FR-PAY-03).</summary>
public class LanePaymentModel
{
    public string Provider { get; set; }

    /// <summary>Paid at a counter/kiosk before driving to the exit: opens an Exit Window.</summary>
    public bool Prepaid { get; set; }
}

public class PaymentResponseModel
{
    public long Id { get; set; }

    public string Code { get; set; }

    /// <summary>"VNPay", "MoMo" or null for cash.</summary>
    public string Provider { get; set; }

    public string Method { get; set; }

    public string Purpose { get; set; }

    public string Channel { get; set; }

    public decimal Amount { get; set; }

    public string Status { get; set; }

    /// <summary>Hosted payment page to redirect to (or show as a QR at the lane).</summary>
    public string PayUrl { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? PaidAt { get; set; }

    public long? ReservationId { get; set; }

    public long? SessionId { get; set; }

    public decimal RefundedAmount { get; set; }

    public long? RefundOfPaymentId { get; set; }

    public string Message { get; set; }

    /// <summary>Signed Exit Pass after an app/kiosk prepayment (FR-PAY-07), for gates that are offline.</summary>
    public string ExitPass { get; set; }

    public DateTime? ExitWindowEndsAt { get; set; }

    /// <summary>BR-11: when the Staff may switch to another method if no callback came.</summary>
    public DateTime? CallbackDeadline { get; set; }
}

/// <summary>Result of a VNPay / MoMo return or IPN after the signature check.</summary>
public class PaymentCallbackResultModel
{
    public bool SignatureValid { get; set; }

    /// <summary>"Applied", "AlreadyProcessed", "Failed", "NotFound", "InvalidAmount", "InvalidSignature", "Refunded".</summary>
    public string Outcome { get; set; }

    public string Message { get; set; }

    public PaymentResponseModel Payment { get; set; }
}

/// <summary>Request to top up XXIV Wallet using VNPay or MoMo.</summary>
public class TopUpWalletModel
{
    public decimal Amount { get; set; }

    public string Provider { get; set; } = "VNPay";

    public string ReturnUrl { get; set; }
}

/// <summary>Request to withdraw money from XXIV Wallet.</summary>
public class WithdrawWalletModel
{
    public decimal Amount { get; set; }

    public string Method { get; set; } = "bank";

    public string BankName { get; set; }

    public string BankAccount { get; set; }
}

public class WalletResponseModel
{
    public decimal Balance { get; set; }

    public decimal HoldAmount { get; set; }

    public string Currency { get; set; } = "VND";

    public string Status { get; set; } = "Active";

    /// <summary>Most the customer can top up online per calendar day.</summary>
    public decimal DailyTopUpLimit { get; set; }

    /// <summary>What is left of today's top-up limit (paid and still-payable top-ups count).</summary>
    public decimal TopUpRemainingToday { get; set; }

    /// <summary>Smallest online top-up accepted.</summary>
    public decimal MinTopUp { get; set; }

    public IReadOnlyList<WalletTransactionResponseModel> RecentTransactions { get; set; } = [];
}

public class WalletTransactionResponseModel
{
    public string Id { get; set; }

    public string Title { get; set; }

    public string Subtitle { get; set; }

    public decimal Amount { get; set; }

    public string Status { get; set; }

    public string Type { get; set; }

    public string Method { get; set; }

    public DateTime CreatedAt { get; set; }
}
