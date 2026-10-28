using System.Collections.Generic;

namespace PMS.Application.Models.Reports;

public class ParkingLotRevenueModel
{
    public int ParkingLotId { get; set; }
    public string ParkingLotName { get; set; }
    public decimal TotalRevenue { get; set; }
    public int TotalSessions { get; set; }
    public string ManagerUserId { get; set; }
}

public class PaymentMethodBreakdownModel
{
    /// <summary>Frontend payment method category: "CASH", "QR_BANKING", "RFID_WALLET", or "OTHER".</summary>
    public string Method { get; set; }

    /// <summary>Net revenue in VND collected via this method for eligible sessions.</summary>
    public decimal Amount { get; set; }

    /// <summary>Percentage share of total revenue rounded to 1 decimal place.</summary>
    public decimal Percentage { get; set; }
}

public class ManagerRevenueReportResponse
{
    public string ManagerUserId { get; set; }
    public string StartDate { get; set; }
    public string EndDate { get; set; }
    public decimal TotalRevenue { get; set; }
    public int TotalSessions { get; set; }
    public IReadOnlyList<ParkingLotRevenueModel> ByLot { get; set; } = [];
    public IReadOnlyList<PaymentMethodBreakdownModel> PaymentMethods { get; set; } = [];
}

public class AdminRevenueReportResponse
{
    public string StartDate { get; set; }
    public string EndDate { get; set; }
    public decimal SystemTotalRevenue { get; set; }
    public int SystemTotalSessions { get; set; }
    public int ActiveLotsCount { get; set; }
    public IReadOnlyList<ParkingLotRevenueModel> ByLot { get; set; } = [];
    public IReadOnlyList<PaymentMethodBreakdownModel> PaymentMethods { get; set; } = [];
}
