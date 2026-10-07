using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class PricingRule
{
    public int Id { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }

    public int? ParkingLotId { get; set; }

    public int? ZoneId { get; set; }

    public int VehicleTypeId { get; set; }

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

    public string Currency { get; set; }

    public int Priority { get; set; }

    public DateTime EffectiveFrom { get; set; }

    public DateTime? EffectiveTo { get; set; }

    public string Status { get; set; }

    public int VersionNo { get; set; }

    public string ApprovedBy { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public string CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
