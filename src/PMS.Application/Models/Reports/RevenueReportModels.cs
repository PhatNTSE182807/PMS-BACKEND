using System.Collections.Generic;

namespace PMS.Application.Models.Reports;

public class ParkingLotRevenueModel
{
    public int ParkingLotId { get; set; }
    public string ParkingLotName { get; set; }
    /// <summary>Fees collected minus <see cref="Compensation"/>.</summary>
    public decimal TotalRevenue { get; set; }
    /// <summary>Compensations the lot paid customers whose booked bay a walk-in took.</summary>
    public decimal Compensation { get; set; }
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
    /// <summary>Compensations paid in the period, already subtracted from <see cref="TotalRevenue"/>.</summary>
    public decimal TotalCompensation { get; set; }
    public int TotalSessions { get; set; }
    public IReadOnlyList<ParkingLotRevenueModel> ByLot { get; set; } = [];
    public IReadOnlyList<PaymentMethodBreakdownModel> PaymentMethods { get; set; } = [];
}

public class AdminRevenueReportResponse
{
    public string StartDate { get; set; }
    public string EndDate { get; set; }
    public decimal SystemTotalRevenue { get; set; }
    /// <summary>Compensations paid in the period, already subtracted from <see cref="SystemTotalRevenue"/>.</summary>
    public decimal SystemTotalCompensation { get; set; }
    public int SystemTotalSessions { get; set; }
    public int ActiveLotsCount { get; set; }
    public IReadOnlyList<ParkingLotRevenueModel> ByLot { get; set; } = [];
    public IReadOnlyList<PaymentMethodBreakdownModel> PaymentMethods { get; set; } = [];

    /// <summary>Revenue and sessions of cars (4 and 7 seats) and motorbikes; OTHER only when above 0.</summary>
    public IReadOnlyList<VehicleKindRevenueModel> ByVehicleType { get; set; } = [];
}

/// <summary>Revenue of one vehicle family: "CAR" (CAR_4 and CAR_7), "MOTORBIKE" or "OTHER" (no vehicle type recorded).</summary>
public class VehicleKindRevenueModel
{
    public string VehicleType { get; set; }
    public decimal TotalRevenue { get; set; }
    public int TotalSessions { get; set; }
}

/// <summary>Settled revenue of a date range cut into days, weeks (Monday to Sunday), months or years.</summary>
public class AdminRevenueTimeSeriesResponse
{
    public string StartDate { get; set; }
    public string EndDate { get; set; }

    /// <summary>"DAY", "WEEK", "MONTH" or "YEAR".</summary>
    public string Granularity { get; set; }

    /// <summary>Every period of the range, oldest first, empty ones included; the first and last are clipped to the range.</summary>
    public IReadOnlyList<RevenueTimeSeriesPointModel> Points { get; set; } = [];
}

public class RevenueTimeSeriesPointModel
{
    /// <summary>First day of the period (yyyy-MM-dd).</summary>
    public string StartDate { get; set; }

    /// <summary>Last day of the period, inclusive (yyyy-MM-dd).</summary>
    public string EndDate { get; set; }

    /// <summary>Fees minus <see cref="Compensation"/> (car and motorbike revenue are net of their own compensations too).</summary>
    public decimal TotalRevenue { get; set; }
    /// <summary>Compensations paid in the period to customers whose booked bay a walk-in took.</summary>
    public decimal Compensation { get; set; }
    public int TotalSessions { get; set; }
    public decimal CarRevenue { get; set; }
    public int CarSessions { get; set; }
    public decimal MotorbikeRevenue { get; set; }
    public int MotorbikeSessions { get; set; }

    /// <summary>Lots with at least one settled session or compensation in the period, sorted by name.</summary>
    public IReadOnlyList<RevenueTimeSeriesLotModel> ByLot { get; set; } = [];
}

public class RevenueTimeSeriesLotModel
{
    public int ParkingLotId { get; set; }
    public string ParkingLotName { get; set; }
    /// <summary>Fees minus <see cref="Compensation"/>.</summary>
    public decimal TotalRevenue { get; set; }
    public decimal Compensation { get; set; }
    public int TotalSessions { get; set; }
    public int CarSessions { get; set; }
    public int MotorbikeSessions { get; set; }
}
