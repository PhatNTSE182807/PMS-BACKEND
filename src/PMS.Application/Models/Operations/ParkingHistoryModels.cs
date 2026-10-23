using System;
using System.Collections.Generic;

namespace PMS.Application.Models.Operations;

/// <summary>Filter and page of the signed-in customer's parking history.</summary>
public class ParkingHistoryQuery
{
    /// <summary>1-based page number.</summary>
    public int Page { get; set; } = 1;

    /// <summary>Rows per page, 1..50 (default 10).</summary>
    public int PageSize { get; set; } = 10;

    /// <summary>Only sessions that ended on or after this time (UTC).</summary>
    public DateTime? From { get; set; }

    /// <summary>Only sessions that ended before this time (UTC).</summary>
    public DateTime? To { get; set; }

    /// <summary>Only sessions of this vehicle.</summary>
    public Guid? VehicleId { get; set; }
}

/// <summary>One page of past sessions, newest exit first, with totals over the whole filter.</summary>
public class ParkingHistoryPageModel
{
    public List<ParkingHistoryItemModel> Items { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    /// <summary>Fees of every session matching the filter (not only this page).</summary>
    public decimal TotalSpent { get; set; }
}

/// <summary>A finished parking session as the customer sees it in the history table.</summary>
public class ParkingHistoryItemModel
{
    public long SessionId { get; set; }
    public string SessionCode { get; set; }
    /// <summary>Completed, AbnormalClosed or EmergencyExit.</summary>
    public string Status { get; set; }
    /// <summary>WalkIn, Reservation or Whitelist.</summary>
    public string SessionType { get; set; }
    public DateTime EntryTime { get; set; }
    public DateTime? ExitTime { get; set; }
    public int DurationMinutes { get; set; }

    public int ParkingLotId { get; set; }
    public string LotName { get; set; }
    public string LotDistrict { get; set; }
    public string LotAddress { get; set; }
    public string ZoneName { get; set; }
    public string SlotCode { get; set; }

    public Guid? VehicleId { get; set; }
    public string PlateNumber { get; set; }
    public string VehicleTypeName { get; set; }
    public string ReservationCode { get; set; }

    /// <summary>Total parking fee after discounts, before the deposit.</summary>
    public decimal FinalFee { get; set; }
    /// <summary>Reservation deposit counted towards the fee.</summary>
    public decimal DepositApplied { get; set; }
    /// <summary>Money paid for the session itself (cash and online).</summary>
    public decimal PaidAmount { get; set; }
    /// <summary>Invoice number of the receipt, or the session code when nothing was invoiced.</summary>
    public string ReceiptNumber { get; set; }
}

/// <summary>An e-receipt of a finished session: the history row plus the fee breakdown and the payments.</summary>
public class ParkingReceiptModel : ParkingHistoryItemModel
{
    public string CustomerName { get; set; }
    public string LotPhone { get; set; }
    public decimal OriginalFee { get; set; }
    public decimal DiscountAmount { get; set; }
    /// <summary>Why the fee was zero (grace period, whitelist...), when it was.</summary>
    public string ZeroFeeReason { get; set; }
    public DateTime IssuedAt { get; set; }
    public List<ParkingReceiptPaymentModel> Payments { get; set; } = [];
}

/// <summary>A settled payment on a receipt: the reservation deposit or the parking fee.</summary>
public class ParkingReceiptPaymentModel
{
    public string PaymentCode { get; set; }
    /// <summary>Deposit or ParkingFee.</summary>
    public string Purpose { get; set; }
    /// <summary>Cash or Online.</summary>
    public string Method { get; set; }
    /// <summary>VNPay or MoMo for online payments.</summary>
    public string Provider { get; set; }
    public decimal Amount { get; set; }
    public DateTime? PaidAt { get; set; }
}
