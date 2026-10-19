using System;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// Payment fields for the three payment points (SRS FR-PAY-02/03/07..10): what is paid for, who
/// collected cash, the gateway's transaction number and refunds. Kept apart from the scaffolded columns.
/// </summary>
public partial class Payment
{
    /// <summary>"Deposit", "ParkingFee" or "Refund".</summary>
    public string Purpose { get; set; }

    public long? ReservationId { get; set; }

    public long? SessionId { get; set; }

    public int? ParkingLotId { get; set; }

    /// <summary>Customer account that paid online, if any (dbo.Users.Id).</summary>
    public string CustomerUserId { get; set; }

    /// <summary>Staff who took the cash (FR-PAY-01), or who started the lane QR.</summary>
    public string CollectedByUserId { get; set; }

    /// <summary>"App", "Lane" or "Kiosk": only App and Kiosk payments open an Exit Window (FR-PAY-07).</summary>
    public string Channel { get; set; }

    /// <summary>The gateway link stops being valid then.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>For a refund row: the payment it returns money from.</summary>
    public long? RefundOfPaymentId { get; set; }

    /// <summary>For a paid row: how much of it was refunded so far.</summary>
    public decimal RefundedAmount { get; set; }

    /// <summary>Gateway transaction number (vnp_TransactionNo / MoMo transId), needed for refunds.</summary>
    public string GatewayTransactionNo { get; set; }

    public string FailureReason { get; set; }

    public string PayUrl { get; set; }

    /// <summary>Last time the gateway was asked for the status (FR-PAY-08).</summary>
    public DateTime? LastCheckedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
