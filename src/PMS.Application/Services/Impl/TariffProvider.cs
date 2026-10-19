using Microsoft.EntityFrameworkCore;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Finds the tariff of a session: the active PricingRule in force at the entry time (BR-22), most
/// specific first (zone → lot → whole chain), then priority and version; otherwise the configured default.
/// </summary>
public class TariffProvider(PmsDbContext pmsContext, ParkingOptions options)
{
    private static readonly string[] UsableStatuses = ["Active", "Approved", "Published"];

    public async Task<Tariff> ForAsync(int lotId, int? zoneId, int? vehicleTypeId, DateTime entryUtc)
    {
        if (vehicleTypeId != null)
        {
            var rules = await pmsContext.PricingRules.AsNoTracking()
                .Where(r => r.VehicleTypeId == vehicleTypeId && UsableStatuses.Contains(r.Status)
                            && (r.ParkingLotId == lotId || r.ParkingLotId == null)
                            && r.EffectiveFrom <= entryUtc && (r.EffectiveTo == null || r.EffectiveTo > entryUtc))
                .ToListAsync();

            // FR-PRC-01: zone prices only apply to reserved bays, i.e. when the session has a zone
            var rule = rules
                .Where(r => r.ZoneId == null || r.ZoneId == zoneId)
                .OrderByDescending(r => r.ZoneId != null)
                .ThenByDescending(r => r.ParkingLotId != null)
                .ThenByDescending(r => r.Priority)
                .ThenByDescending(r => r.VersionNo)
                .FirstOrDefault();
            if (rule != null)
                return FromRule(rule);
        }

        var code = vehicleTypeId == null
            ? null
            : await pmsContext.VehicleTypes.Where(v => v.Id == vehicleTypeId).Select(v => v.Code).FirstOrDefaultAsync();
        var tariffs = options.EffectiveTariffs;
        var fallback = tariffs.FirstOrDefault(t => string.Equals(t.VehicleTypeCode, code, StringComparison.OrdinalIgnoreCase))
                       ?? tariffs.FirstOrDefault(t => ParkingInventory.Family(t.VehicleTypeCode) == ParkingInventory.Family(code))
                       ?? tariffs[0];
        return new Tariff
        {
            GraceMinutes = fallback.GraceMinutes,
            BlockMinutes = fallback.BlockMinutes,
            BlockPrice = fallback.BlockPrice,
            MaxDailyFee = fallback.MaxDailyFee,
            LostTicketFee = fallback.LostTicketFee
        };
    }

    public static Tariff FromRule(PricingRule rule) => new()
    {
        ChargeModel = string.Equals(rule.ChargeModel, "Hourly", StringComparison.OrdinalIgnoreCase) ? "Block" : rule.ChargeModel,
        GraceMinutes = rule.GraceMinutes,
        EntryFee = rule.EntryFee,
        FirstBlockMinutes = rule.FirstBlockMinutes,
        FirstBlockPrice = rule.FirstBlockPrice,
        BlockMinutes = rule.BlockMinutes ?? 60,
        BlockPrice = rule.BlockPrice ?? 0,
        MaxDailyFee = rule.MaxDailyFee,
        DayStart = rule.DayStartTime,
        NightStart = rule.NightStartTime,
        DayPrice = rule.DayPrice,
        NightPrice = rule.NightPrice,
        LostTicketFee = rule.LostTicketFee,
        RuleId = rule.Id,
        Version = rule.VersionNo
    };
}
