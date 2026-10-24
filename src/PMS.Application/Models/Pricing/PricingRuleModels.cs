using PMS.Application.Models.Operations;
using System;
using System.Collections.Generic;

namespace PMS.Application.Models.Pricing;

/// <summary>
/// Detailed DTO for a pricing rule / tariff configuration (SRS section 5, BR-09, BR-22).
/// </summary>
public class PricingRuleModel
{
    public int Id { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }

    public int? ParkingLotId { get; set; }

    public int? ZoneId { get; set; }

    public int VehicleTypeId { get; set; }

    public string VehicleTypeCode { get; set; }

    public string VehicleTypeName { get; set; }

    /// <summary>"Block" (default), "Hourly", "PerEntry", or "DayNight".</summary>
    public string ChargeModel { get; set; }

    public decimal EntryFee { get; set; }

    public int? FirstBlockMinutes { get; set; }

    public decimal? FirstBlockPrice { get; set; }

    public int? BlockMinutes { get; set; }

    public decimal? BlockPrice { get; set; }

    public TimeOnly? DayStartTime { get; set; }

    public TimeOnly? NightStartTime { get; set; }

    public decimal? DayPrice { get; set; }

    public decimal? NightPrice { get; set; }

    public decimal? MaxDailyFee { get; set; }

    public int GraceMinutes { get; set; }

    public decimal OvernightSurcharge { get; set; }

    public int? OverstayAfterHours { get; set; }

    public decimal OverstaySurchargePerDay { get; set; }

    public decimal LostTicketFee { get; set; }

    public decimal NoShowFee { get; set; }

    public string Currency { get; set; } = "VND";

    public int Priority { get; set; }

    public DateTime EffectiveFrom { get; set; }

    public DateTime? EffectiveTo { get; set; }

    /// <summary>"Draft", "Active", "Inactive", "Archived".</summary>
    public string Status { get; set; }

    public int VersionNo { get; set; }

    public string ApprovedBy { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public string CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}

/// <summary>Public price of one vehicle type at a lot (what the gates charge now), for customers.</summary>
public class PublicLotPriceModel
{
    public int VehicleTypeId { get; set; }
    public string VehicleTypeCode { get; set; }
    public string VehicleTypeName { get; set; }
    /// <summary>"Block" (same price all day) or "DayNight" (block price by the hour it starts).</summary>
    public string ChargeModel { get; set; }
    public int GraceMinutes { get; set; }
    public int? FirstBlockMinutes { get; set; }
    public decimal? FirstBlockPrice { get; set; }
    public int BlockMinutes { get; set; }
    public decimal BlockPrice { get; set; }
    public TimeOnly? DayStartTime { get; set; }
    public TimeOnly? NightStartTime { get; set; }
    public decimal? DayPrice { get; set; }
    public decimal? NightPrice { get; set; }
    public decimal? MaxDailyFee { get; set; }
}

/// <summary>Request DTO to create a new pricing rule for a parking lot.</summary>
public class CreatePricingRuleModel
{
    public string Code { get; set; }

    public string Name { get; set; }

    public int? ZoneId { get; set; }

    public int VehicleTypeId { get; set; }

    public string ChargeModel { get; set; } = "Block";

    public decimal EntryFee { get; set; } = 0;

    public int? FirstBlockMinutes { get; set; }

    public decimal? FirstBlockPrice { get; set; }

    public int? BlockMinutes { get; set; } = 60;

    public decimal? BlockPrice { get; set; } = 0;

    public TimeOnly? DayStartTime { get; set; }

    public TimeOnly? NightStartTime { get; set; }

    public decimal? DayPrice { get; set; }

    public decimal? NightPrice { get; set; }

    public decimal? MaxDailyFee { get; set; }

    public int GraceMinutes { get; set; } = 15;

    public decimal OvernightSurcharge { get; set; } = 0;

    public int? OverstayAfterHours { get; set; }

    public decimal OverstaySurchargePerDay { get; set; } = 0;

    public decimal LostTicketFee { get; set; } = 0;

    public decimal NoShowFee { get; set; } = 0;

    public string Currency { get; set; } = "VND";

    public int Priority { get; set; } = 0;

    public DateTime? EffectiveFrom { get; set; }

    public DateTime? EffectiveTo { get; set; }

    public string Status { get; set; } = "Draft";
}

/// <summary>Request DTO to update an existing pricing rule.</summary>
public class UpdatePricingRuleModel
{
    public string Name { get; set; }

    public int? ZoneId { get; set; }

    public int VehicleTypeId { get; set; }

    public string ChargeModel { get; set; } = "Block";

    public decimal EntryFee { get; set; }

    public int? FirstBlockMinutes { get; set; }

    public decimal? FirstBlockPrice { get; set; }

    public int? BlockMinutes { get; set; } = 60;

    public decimal? BlockPrice { get; set; } = 0;

    public TimeOnly? DayStartTime { get; set; }

    public TimeOnly? NightStartTime { get; set; }

    public decimal? DayPrice { get; set; }

    public decimal? NightPrice { get; set; }

    public decimal? MaxDailyFee { get; set; }

    public int GraceMinutes { get; set; } = 15;

    public decimal OvernightSurcharge { get; set; } = 0;

    public int? OverstayAfterHours { get; set; }

    public decimal OverstaySurchargePerDay { get; set; } = 0;

    public decimal LostTicketFee { get; set; } = 0;

    public decimal NoShowFee { get; set; } = 0;

    public int Priority { get; set; } = 0;

    public DateTime EffectiveFrom { get; set; }

    public DateTime? EffectiveTo { get; set; }

    public string Status { get; set; }

    public string Reason { get; set; }
}

/// <summary>Request DTO to change the lifecycle status of a pricing rule.</summary>
public class SetPricingRuleStatusModel
{
    public string Status { get; set; }

    public string Reason { get; set; }
}

/// <summary>Request DTO to simulate checkout pricing before saving changes (fee calculator sandbox).</summary>
public class PricingSimulationRequestModel
{
    public int? VehicleTypeId { get; set; }

    public string VehicleTypeCode { get; set; }

    public int? RuleId { get; set; }

    public DateTime EntryTime { get; set; }

    public DateTime ExitTime { get; set; }

    public decimal DepositAmount { get; set; } = 0;

    public bool IsLostTicket { get; set; } = false;
}

/// <summary>Detailed calculation and settlement output of a pricing simulation.</summary>
public class PricingSimulationResultModel
{
    public int DurationMinutes { get; set; }

    public bool WithinGrace { get; set; }

    public decimal GrossFee { get; set; }

    public decimal LostTicketFee { get; set; }

    public decimal ExitWindowLateFee { get; set; }

    public decimal DepositApplied { get; set; }

    public decimal DepositRefund { get; set; }

    public decimal AmountDue { get; set; }

    public IReadOnlyList<FeeDayModel> Days { get; set; } = [];

    public int? RuleId { get; set; }

    public int? RuleVersion { get; set; }

    public string RuleName { get; set; }
}
