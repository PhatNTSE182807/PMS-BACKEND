using System.Globalization;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Models.Pricing;

/// <summary>
/// The charging fields of a pricing rule: everything that decides what a customer pays. Comparing them tells whether
/// an update changed the price, as opposed to the other fields of the rule (name, zone, vehicle type, priority,
/// validity, status).
/// </summary>
public sealed record PricingRulePrices
{
    public string ChargeModel { get; init; } = "Block";

    public decimal EntryFee { get; init; }

    public int? FirstBlockMinutes { get; init; }

    public decimal? FirstBlockPrice { get; init; }

    public int? BlockMinutes { get; init; }

    public decimal? BlockPrice { get; init; }

    public TimeOnly? DayStartTime { get; init; }

    public TimeOnly? NightStartTime { get; init; }

    public decimal? DayPrice { get; init; }

    public decimal? NightPrice { get; init; }

    public decimal? MaxDailyFee { get; init; }

    public int GraceMinutes { get; init; }

    public decimal OvernightSurcharge { get; init; }

    public int? OverstayAfterHours { get; init; }

    public decimal OverstaySurchargePerDay { get; init; }

    public decimal LostTicketFee { get; init; }

    public decimal NoShowFee { get; init; }

    public static PricingRulePrices From(PricingRule rule) => new()
    {
        ChargeModel = rule.ChargeModel,
        EntryFee = rule.EntryFee,
        FirstBlockMinutes = rule.FirstBlockMinutes,
        FirstBlockPrice = rule.FirstBlockPrice,
        BlockMinutes = rule.BlockMinutes,
        BlockPrice = rule.BlockPrice,
        DayStartTime = rule.DayStartTime,
        NightStartTime = rule.NightStartTime,
        DayPrice = rule.DayPrice,
        NightPrice = rule.NightPrice,
        MaxDailyFee = rule.MaxDailyFee,
        GraceMinutes = rule.GraceMinutes,
        OvernightSurcharge = rule.OvernightSurcharge,
        OverstayAfterHours = rule.OverstayAfterHours,
        OverstaySurchargePerDay = rule.OverstaySurchargePerDay,
        LostTicketFee = rule.LostTicketFee,
        NoShowFee = rule.NoShowFee
    };

    /// <summary>The charging fields of an update request, with the same defaults a saved rule gets.</summary>
    public static PricingRulePrices From(UpdatePricingRuleModel model) => new()
    {
        ChargeModel = string.IsNullOrWhiteSpace(model.ChargeModel) ? "Block" : model.ChargeModel,
        EntryFee = model.EntryFee,
        FirstBlockMinutes = model.FirstBlockMinutes,
        FirstBlockPrice = model.FirstBlockPrice,
        BlockMinutes = model.BlockMinutes ?? 60,
        BlockPrice = model.BlockPrice ?? 0,
        DayStartTime = model.DayStartTime,
        NightStartTime = model.NightStartTime,
        DayPrice = model.DayPrice,
        NightPrice = model.NightPrice,
        MaxDailyFee = model.MaxDailyFee,
        GraceMinutes = model.GraceMinutes,
        OvernightSurcharge = model.OvernightSurcharge,
        OverstayAfterHours = model.OverstayAfterHours,
        OverstaySurchargePerDay = model.OverstaySurchargePerDay,
        LostTicketFee = model.LostTicketFee,
        NoShowFee = model.NoShowFee
    };

    public void ApplyTo(PricingRule rule)
    {
        rule.ChargeModel = ChargeModel;
        rule.EntryFee = EntryFee;
        rule.FirstBlockMinutes = FirstBlockMinutes;
        rule.FirstBlockPrice = FirstBlockPrice;
        rule.BlockMinutes = BlockMinutes;
        rule.BlockPrice = BlockPrice;
        rule.DayStartTime = DayStartTime;
        rule.NightStartTime = NightStartTime;
        rule.DayPrice = DayPrice;
        rule.NightPrice = NightPrice;
        rule.MaxDailyFee = MaxDailyFee;
        rule.GraceMinutes = GraceMinutes;
        rule.OvernightSurcharge = OvernightSurcharge;
        rule.OverstayAfterHours = OverstayAfterHours;
        rule.OverstaySurchargePerDay = OverstaySurchargePerDay;
        rule.LostTicketFee = LostTicketFee;
        rule.NoShowFee = NoShowFee;
    }

    /// <summary>
    /// The price in words, for customer notifications, the same way for every vehicle type:
    /// "5,000 VND for the first 2 hours, then 2,000 VND per hour, at most 20,000 VND a day".
    /// </summary>
    public string Describe()
    {
        static string Money(decimal? value) => $"{(value ?? 0).ToString("N0", CultureInfo.InvariantCulture)} VND";
        // "hour", "4 hours", "90 min"
        static string Span(int minutes) => minutes == 60 ? "hour" : minutes % 60 == 0 ? $"{minutes / 60} hours" : $"{minutes} min";

        if (string.Equals(ChargeModel, "PerEntry", StringComparison.OrdinalIgnoreCase))
            return $"{Money(EntryFee)} per day";

        var minutes = BlockMinutes is > 0 ? BlockMinutes.Value : 60;
        var text = string.Equals(ChargeModel, "DayNight", StringComparison.OrdinalIgnoreCase)
            ? $"{Money(DayPrice)} by day and {Money(NightPrice)} by night per {Span(minutes)}"
            : $"{Money(BlockPrice)} per {Span(minutes)}";
        if (FirstBlockMinutes is > 0 && FirstBlockPrice != null)
            text = $"{Money(FirstBlockPrice)} for the first {Span(FirstBlockMinutes.Value)}, then {text}";
        if (EntryFee > 0)
            text = $"{Money(EntryFee)} on entry, plus {text}";
        if (MaxDailyFee is > 0)
            text += $", at most {Money(MaxDailyFee)} a day";
        return text;
    }
}
