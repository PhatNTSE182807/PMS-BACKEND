using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using PMS.Application.Models.Pricing;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Implements application-level operations for pricing rules, tariffs, and fee simulation (SRS section 5, BR-09, BR-22).
/// Restricts access to parking lots managed by the calling Manager (or all lots for Admin).
/// </summary>
public class PricingRuleService(
    PmsDbContext pmsContext,
    LotScopeService lotScopeService,
    TariffProvider tariffProvider,
    DeferredPriceService deferredPrices,
    ParkingOptions parkingOptions,
    TimeProvider clock,
    ILotPriceNotifier priceNotifier = null,
    ILogger<PricingRuleService> logger = null) : IPricingRuleService
{
    private static readonly string[] ValidStatuses = ["Draft", "Active", "Inactive", "Archived"];

    private async Task TryRecordHistoryAsync(int ruleId, string changeType, string oldJson, string newJson, string reason, DateTime now)
    {
        try
        {
            pmsContext.PricingRuleHistories.Add(new PricingRuleHistory
            {
                PricingRuleId = ruleId,
                ChangeType = changeType,
                OldValuesJson = oldJson,
                NewValuesJson = newJson,
                ChangedBy = lotScopeService.UserId,
                ChangedAt = now,
                Reason = reason
            });
            await pmsContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to record PricingRuleHistory for rule {RuleId} ({ChangeType})", ruleId, changeType);
            foreach (var entry in pmsContext.ChangeTracker.Entries<PricingRuleHistory>().ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    public async Task<IReadOnlyList<PricingRuleModel>> GetPricingRulesAsync(int lotId, string status = null)
    {
        await EnsureLotExistsAndManagedAsync(lotId);

        var query = pmsContext.PricingRules.AsNoTracking()
            .Where(r => r.ParkingLotId == lotId || r.ParkingLotId == null);

        if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "ALL", StringComparison.OrdinalIgnoreCase))
            query = query.Where(r => r.Status == status);
        else
            query = query.Where(r => r.Status != "Archived");

        var rules = await query
            .OrderByDescending(r => r.Priority)
            .ThenByDescending(r => r.CreatedAt)
            .ToListAsync();

        var vehicleTypes = await pmsContext.VehicleTypes.AsNoTracking()
            .ToDictionaryAsync(v => v.Id, v => new { v.Code, v.Name });
        var waiting = await pmsContext.PendingPriceChanges.AsNoTracking()
            .Where(p => p.ParkingLotId == lotId)
            .ToDictionaryAsync(p => p.PricingRuleId);

        return rules.Select(r =>
        {
            vehicleTypes.TryGetValue(r.VehicleTypeId, out var vt);
            return ToModelWithPending(r, vt?.Code, vt?.Name, waiting.GetValueOrDefault(r.Id));
        }).ToList();
    }

    public async Task<PricingRuleModel> GetPricingRuleByIdAsync(int lotId, int ruleId)
    {
        await EnsureLotExistsAndManagedAsync(lotId);

        var rule = await FindRuleAsync(lotId, ruleId, tracking: false);
        var vehicleType = await pmsContext.VehicleTypes.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == rule.VehicleTypeId);
        var waiting = await pmsContext.PendingPriceChanges.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PricingRuleId == rule.Id);

        return ToModelWithPending(rule, vehicleType?.Code, vehicleType?.Name, waiting);
    }

    public async Task<PricingRuleModel> CreatePricingRuleAsync(int lotId, CreatePricingRuleModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        await EnsureLotExistsAndManagedAsync(lotId);

        var vehicleType = await pmsContext.VehicleTypes.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == model.VehicleTypeId && v.IsActive)
            ?? throw new BadRequestException($"Vehicle type with ID {model.VehicleTypeId} does not exist or is inactive.");

        if (model.ZoneId.HasValue)
        {
            var zoneExists = await pmsContext.Zones.AsNoTracking()
                .AnyAsync(z => z.Id == model.ZoneId.Value && z.ParkingLotId == lotId);
            if (!zoneExists)
                throw new BadRequestException($"Zone with ID {model.ZoneId.Value} does not belong to parking lot {lotId}.");
        }

        var now = clock.GetUtcNow().UtcDateTime;

        var duplicateThreshold = now.AddSeconds(-15);
        var recentDuplicate = await pmsContext.PricingRules.AsNoTracking()
            .Where(r => r.ParkingLotId == lotId
                     && r.VehicleTypeId == model.VehicleTypeId
                     && r.Name == model.Name.Trim()
                     && r.CreatedBy == lotScopeService.UserId
                     && r.CreatedAt >= duplicateThreshold)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();

        if (recentDuplicate != null)
        {
            return ToModel(recentDuplicate, vehicleType.Code, vehicleType.Name);
        }

        var code = !string.IsNullOrWhiteSpace(model.Code)
            ? model.Code.Trim().ToUpperInvariant()
            : $"RULE-{lotId}-{vehicleType.Code}-{now:yyyyMMddHHmmss}";

        var codeExists = await pmsContext.PricingRules.AsNoTracking()
            .AnyAsync(r => r.ParkingLotId == lotId && r.Code == code);
        if (codeExists)
            throw new ConflictException($"A pricing rule with code '{code}' already exists for lot {lotId}.");

        var status = string.IsNullOrWhiteSpace(model.Status) ? "Draft" : model.Status;
        if (!ValidStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            throw new BadRequestException($"Invalid status '{status}'. Valid statuses: {string.Join(", ", ValidStatuses)}.");

        var rule = new PricingRule
        {
            Code = code,
            Name = model.Name.Trim(),
            ParkingLotId = lotId,
            ZoneId = model.ZoneId,
            VehicleTypeId = model.VehicleTypeId,
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
            NoShowFee = model.NoShowFee,
            Currency = string.IsNullOrWhiteSpace(model.Currency) ? "VND" : model.Currency.ToUpperInvariant(),
            Priority = model.Priority,
            EffectiveFrom = model.EffectiveFrom ?? now,
            EffectiveTo = model.EffectiveTo,
            Status = status,
            VersionNo = 1,
            CreatedBy = lotScopeService.UserId,
            CreatedAt = now
        };

        if (string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase))
        {
            rule.ApprovedBy = lotScopeService.UserId;
            rule.ApprovedAt = now;
        }

        pmsContext.PricingRules.Add(rule);
        await pmsContext.SaveChangesAsync();

        var resultModel = ToModel(rule, vehicleType.Code, vehicleType.Name);

        await TryRecordHistoryAsync(rule.Id, "Create", null, JsonSerializer.Serialize(resultModel), "Created initial pricing rule", now);

        return resultModel;
    }

    public async Task<PricingRuleModel> UpdatePricingRuleAsync(int lotId, int ruleId, UpdatePricingRuleModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var lot = await EnsureLotExistsAndManagedAsync(lotId);

        var existing = await FindRuleAsync(lotId, ruleId, tracking: false);
        if (existing.ParkingLotId != null && existing.ParkingLotId != lotId)
            throw new ForbiddenException("Cannot modify a pricing rule that does not belong to this parking lot.");

        // A rule of the whole system belongs to no single lot, so there is no "empty lot" to wait for:
        // a Manager may not change it (an Admin still can, and it takes effect at once)
        var sharedRule = existing.ParkingLotId == null;
        if (sharedRule && lotScopeService.Role != "Admin")
            throw new BadRequestException("The shared pricing rule of the whole system cannot be changed here.");

        var vehicleType = await pmsContext.VehicleTypes.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == model.VehicleTypeId && v.IsActive)
            ?? throw new BadRequestException($"Vehicle type with ID {model.VehicleTypeId} does not exist or is inactive.");

        if (model.ZoneId.HasValue)
        {
            var zoneExists = await pmsContext.Zones.AsNoTracking()
                .AnyAsync(z => z.Id == model.ZoneId.Value && z.ParkingLotId == lotId);
            if (!zoneExists)
                throw new BadRequestException($"Zone with ID {model.ZoneId.Value} does not belong to parking lot {lotId}.");
        }

        if (!string.IsNullOrWhiteSpace(model.Status) && !ValidStatuses.Contains(model.Status, StringComparer.OrdinalIgnoreCase))
            throw new BadRequestException($"Invalid status '{model.Status}'. Valid statuses: {string.Join(", ", ValidStatuses)}.");

        var now = clock.GetUtcNow().UtcDateTime;
        var proposed = PricingRulePrices.From(model);

        var (rule, oldModel, outcome) = await LotConcurrency.RunAsync(pmsContext, async () =>
        {
            // Concurrency: the lot row is read first and saved last. The decision "is the lot empty?" rests on
            // its OccupiedBays, which the row's RowVersion protects. If a vehicle enters or leaves (or another
            // price request is saved) between this read and our save, SaveChangesAsync throws
            // DbUpdateConcurrencyException and writes nothing; RunAsync undoes this attempt and runs it again
            // with the new count, which may turn "apply now" into "wait" or the other way round.
            var trackedLot = await pmsContext.ParkingLots.FirstAsync(p => p.Id == lotId);
            var tracked = await FindRuleAsync(lotId, ruleId, tracking: true);
            var before = ToModel(tracked, vehicleType.Code, vehicleType.Name);

            // Everything that is not a charging field takes effect at once
            tracked.Name = model.Name.Trim();
            tracked.ZoneId = model.ZoneId;
            tracked.VehicleTypeId = model.VehicleTypeId;
            tracked.Priority = model.Priority;
            tracked.EffectiveFrom = model.EffectiveFrom;
            tracked.EffectiveTo = model.EffectiveTo;
            if (!string.IsNullOrWhiteSpace(model.Status))
                tracked.Status = model.Status;

            // The charging fields go live now (empty lot) or wait for the last vehicle to leave
            var result = await deferredPrices.StageUpdateAsync(trackedLot, tracked, proposed, lotScopeService.UserId, applyNow: sharedRule);

            // A request that only waits leaves the rule as it is, so it keeps its version
            pmsContext.ChangeTracker.DetectChanges();
            if (result != PriceUpdateOutcome.Pending || pmsContext.Entry(tracked).State == EntityState.Modified)
            {
                tracked.VersionNo += 1;
                tracked.UpdatedAt = now;
            }

            LotConcurrency.Touch(pmsContext, trackedLot);
            await pmsContext.SaveChangesAsync();
            return (tracked, before, result);
        });

        var newModel = ToModel(rule, vehicleType.Code, vehicleType.Name);
        newModel.PriceUpdateStatus = outcome switch
        {
            PriceUpdateOutcome.Applied => PriceUpdateStatuses.Applied,
            PriceUpdateOutcome.Pending => PriceUpdateStatuses.Pending,
            _ => PriceUpdateStatuses.Unchanged
        };
        var notice = new LotPriceNotice(lotId, lot.Name, vehicleType.Name, proposed.Describe());

        switch (outcome)
        {
            case PriceUpdateOutcome.Pending:
                newModel.PendingPrice = ToPendingModel(await pmsContext.PendingPriceChanges.AsNoTracking().FirstOrDefaultAsync(p => p.PricingRuleId == rule.Id));
                await TryRecordHistoryAsync(rule.Id, "PriceDeferred", JsonSerializer.Serialize(PricingRulePrices.From(rule)), JsonSerializer.Serialize(proposed),
                    model.Reason ?? "Price change waits until the lot is empty", now);
                await TryNotifyAsync(() => priceNotifier?.PriceScheduledAsync(notice));
                break;

            case PriceUpdateOutcome.Applied:
                await TryRecordHistoryAsync(rule.Id, "Update", JsonSerializer.Serialize(oldModel), JsonSerializer.Serialize(newModel), model.Reason ?? "Updated pricing rule parameters", now);
                await TryNotifyAsync(() => priceNotifier?.PriceAppliedAsync(notice));
                break;

            default:
                await TryRecordHistoryAsync(rule.Id, "Update", JsonSerializer.Serialize(oldModel), JsonSerializer.Serialize(newModel), model.Reason ?? "Updated pricing rule parameters", now);
                break;
        }

        return newModel;
    }

    /// <summary>Customers are told after the change is saved; a failed notice never undoes or fails the update.</summary>
    private async Task TryNotifyAsync(Func<Task> notify)
    {
        try
        {
            var task = notify();
            if (task != null)
                await task;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not announce the price change");
        }
    }

    public async Task<PricingRuleModel> SetPricingRuleStatusAsync(int lotId, int ruleId, SetPricingRuleStatusModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (string.IsNullOrWhiteSpace(model.Status) || !ValidStatuses.Contains(model.Status, StringComparer.OrdinalIgnoreCase))
            throw new BadRequestException($"Invalid status '{model.Status}'. Valid statuses: {string.Join(", ", ValidStatuses)}.");

        await EnsureLotExistsAndManagedAsync(lotId);
        var rule = await FindRuleAsync(lotId, ruleId, tracking: true);

        if (rule.ParkingLotId != null && rule.ParkingLotId != lotId)
            throw new ForbiddenException("Cannot modify status for a pricing rule that does not belong to this parking lot.");

        var now = clock.GetUtcNow().UtcDateTime;
        var oldStatus = rule.Status;

        rule.Status = model.Status;
        rule.UpdatedAt = now;

        if (string.Equals(model.Status, "Active", StringComparison.OrdinalIgnoreCase))
        {
            rule.ApprovedBy = lotScopeService.UserId;
            rule.ApprovedAt = now;
        }

        await pmsContext.SaveChangesAsync();

        await TryRecordHistoryAsync(rule.Id, "StatusChange", JsonSerializer.Serialize(new { Status = oldStatus }), JsonSerializer.Serialize(new { Status = model.Status }), model.Reason ?? $"Status changed from {oldStatus} to {model.Status}", now);

        var vehicleType = await pmsContext.VehicleTypes.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == rule.VehicleTypeId);

        return ToModel(rule, vehicleType?.Code, vehicleType?.Name);
    }

    public async Task DeletePricingRuleAsync(int lotId, int ruleId)
    {
        await EnsureLotExistsAndManagedAsync(lotId);
        var rule = await FindRuleAsync(lotId, ruleId, tracking: true);

        if (rule.ParkingLotId != null && rule.ParkingLotId != lotId)
            throw new ForbiddenException("Cannot delete a pricing rule that does not belong to this parking lot.");

        var now = clock.GetUtcNow().UtcDateTime;
        var oldStatus = rule.Status;

        rule.Status = "Archived";
        rule.UpdatedAt = now;

        await pmsContext.SaveChangesAsync();

        await TryRecordHistoryAsync(rule.Id, "Delete", JsonSerializer.Serialize(new { Status = oldStatus }), JsonSerializer.Serialize(new { Status = "Archived" }), "Pricing rule archived", now);
    }

    public async Task<IReadOnlyList<PublicLotPriceModel>> GetPublicPricesAsync(int lotId)
    {
        var lotExists = await pmsContext.ParkingLots.AsNoTracking().AnyAsync(p => p.Id == lotId && !p.IsDeleted);
        if (!lotExists)
            throw new NotFoundException($"Parking lot {lotId} was not found.");

        var now = clock.GetUtcNow().UtcDateTime;
        var types = await pmsContext.VehicleTypes.AsNoTracking()
            .Where(v => v.IsActive)
            .OrderBy(v => v.SortOrder)
            .ToListAsync();

        var prices = new List<PublicLotPriceModel>();
        foreach (var type in types)
        {
            // Same choice as a checkout now: the lot's Active rule for the type, else the configured default
            var tariff = await tariffProvider.ForAsync(lotId, null, type.Id, now);
            var dayNight = string.Equals(tariff.ChargeModel, "DayNight", StringComparison.OrdinalIgnoreCase);
            prices.Add(new PublicLotPriceModel
            {
                VehicleTypeId = type.Id,
                VehicleTypeCode = type.Code,
                VehicleTypeName = type.Name,
                ChargeModel = dayNight ? "DayNight" : "Block",
                GraceMinutes = tariff.GraceMinutes,
                FirstBlockMinutes = tariff.FirstBlockMinutes,
                FirstBlockPrice = tariff.FirstBlockPrice,
                BlockMinutes = tariff.BlockMinutes,
                BlockPrice = tariff.BlockPrice,
                DayStartTime = dayNight ? tariff.DayStart : null,
                NightStartTime = dayNight ? tariff.NightStart : null,
                DayPrice = dayNight ? tariff.DayPrice : null,
                NightPrice = dayNight ? tariff.NightPrice : null,
                MaxDailyFee = tariff.MaxDailyFee
            });
        }

        return prices;
    }

    public async Task<PricingSimulationResultModel> SimulatePricingAsync(int lotId, PricingSimulationRequestModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var lot = await EnsureLotExistsAndManagedAsync(lotId);

        if (model.ExitTime < model.EntryTime)
            throw new BadRequestException("Exit time must be greater than or equal to entry time.");

        Tariff tariff;
        string ruleName;

        if (model.RuleId.HasValue)
        {
            var rule = await pmsContext.PricingRules.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == model.RuleId.Value && (r.ParkingLotId == lotId || r.ParkingLotId == null))
                ?? throw new NotFoundException($"Pricing rule {model.RuleId.Value} not found for lot {lotId}.");

            tariff = TariffProvider.FromRule(rule);
            ruleName = rule.Name;
        }
        else
        {
            int? vehicleTypeId = model.VehicleTypeId;
            if (vehicleTypeId == null && !string.IsNullOrWhiteSpace(model.VehicleTypeCode))
            {
                vehicleTypeId = await pmsContext.VehicleTypes.AsNoTracking()
                    .Where(v => v.Code == model.VehicleTypeCode)
                    .Select(v => (int?)v.Id)
                    .FirstOrDefaultAsync();
            }

            tariff = await tariffProvider.ForAsync(lotId, null, vehicleTypeId, model.EntryTime);

            ruleName = tariff.RuleId.HasValue
                ? await pmsContext.PricingRules.AsNoTracking()
                    .Where(r => r.Id == tariff.RuleId.Value)
                    .Select(r => r.Name)
                    .FirstOrDefaultAsync() ?? "Active Rule"
                : "Configured Default";
        }

        var timeZone = ParkingText.FindTimeZone(lot.TimeZoneId ?? parkingOptions.TimeZoneId);
        var breakdown = FeeCalculator.Calculate(tariff, model.EntryTime, model.ExitTime, timeZone);
        var lostFee = model.IsLostTicket ? tariff.LostTicketFee : 0m;
        var settlement = FeeCalculator.Settle(
            breakdown.Total,
            model.DepositAmount,
            alreadyPaid: 0m,
            exitWindowMissed: false,
            exitWindowLateFee: parkingOptions.Gate.ExitWindowLateFee,
            lostTicketFee: lostFee);

        return new PricingSimulationResultModel
        {
            DurationMinutes = breakdown.DurationMinutes,
            WithinGrace = breakdown.WithinGrace,
            GrossFee = breakdown.Total,
            LostTicketFee = settlement.LostTicketFee,
            ExitWindowLateFee = settlement.ExitWindowLateFee,
            DepositApplied = settlement.DepositApplied,
            DepositRefund = settlement.DepositRefund,
            AmountDue = settlement.AmountDue,
            Days = breakdown.Days.Select(d => new FeeDayModel
            {
                Date = d.Date,
                Minutes = d.Minutes,
                Blocks = d.Blocks,
                Amount = d.Amount,
                Capped = d.Capped
            }).ToList(),
            RuleId = tariff.RuleId,
            RuleVersion = tariff.Version,
            RuleName = ruleName
        };
    }

    private async Task<ParkingLot> EnsureLotExistsAndManagedAsync(int lotId)
    {
        var lot = await pmsContext.ParkingLots.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == lotId && !p.IsDeleted)
            ?? throw new NotFoundException($"Parking lot with ID {lotId} not found.");

        await lotScopeService.EnsureCanManageAsync(lotId);
        return lot;
    }

    private async Task<PricingRule> FindRuleAsync(int lotId, int ruleId, bool tracking)
    {
        var query = tracking ? pmsContext.PricingRules.AsQueryable() : pmsContext.PricingRules.AsNoTracking();
        var rule = await query.FirstOrDefaultAsync(r => r.Id == ruleId && (r.ParkingLotId == lotId || r.ParkingLotId == null));

        if (rule == null)
            throw new NotFoundException($"Pricing rule with ID {ruleId} not found for lot {lotId}.");

        return rule;
    }

    private static PendingPriceModel ToPendingModel(PendingPriceChange change)
    {
        if (change == null)
            return null;

        try
        {
            var prices = JsonSerializer.Deserialize<PricingRulePrices>(change.ProposedValuesJson);
            return new PendingPriceModel
            {
                Prices = prices,
                Summary = prices.Describe(),
                RevisionNo = change.RevisionNo,
                RequestedBy = change.RequestedBy,
                RequestedAt = DateTime.SpecifyKind(change.RequestedAt, DateTimeKind.Utc),
                UpdatedAt = change.UpdatedAt.HasValue ? DateTime.SpecifyKind(change.UpdatedAt.Value, DateTimeKind.Utc) : null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The rule as the API shows it, with the price that is waiting for an empty lot (if any).</summary>
    private static PricingRuleModel ToModelWithPending(PricingRule r, string vehicleTypeCode, string vehicleTypeName, PendingPriceChange pending)
    {
        var model = ToModel(r, vehicleTypeCode, vehicleTypeName);
        model.PendingPrice = ToPendingModel(pending);
        return model;
    }

    internal static PricingRuleModel ToModel(PricingRule r, string vehicleTypeCode = null, string vehicleTypeName = null) => new()
    {
        Id = r.Id,
        Code = r.Code,
        Name = r.Name,
        ParkingLotId = r.ParkingLotId,
        ZoneId = r.ZoneId,
        VehicleTypeId = r.VehicleTypeId,
        VehicleTypeCode = vehicleTypeCode,
        VehicleTypeName = vehicleTypeName,
        ChargeModel = r.ChargeModel,
        EntryFee = r.EntryFee,
        FirstBlockMinutes = r.FirstBlockMinutes,
        FirstBlockPrice = r.FirstBlockPrice,
        BlockMinutes = r.BlockMinutes,
        BlockPrice = r.BlockPrice,
        DayStartTime = r.DayStartTime,
        NightStartTime = r.NightStartTime,
        DayPrice = r.DayPrice,
        NightPrice = r.NightPrice,
        MaxDailyFee = r.MaxDailyFee,
        GraceMinutes = r.GraceMinutes,
        OvernightSurcharge = r.OvernightSurcharge,
        OverstayAfterHours = r.OverstayAfterHours,
        OverstaySurchargePerDay = r.OverstaySurchargePerDay,
        LostTicketFee = r.LostTicketFee,
        NoShowFee = r.NoShowFee,
        Currency = r.Currency ?? "VND",
        Priority = r.Priority,
        EffectiveFrom = DateTime.SpecifyKind(r.EffectiveFrom, DateTimeKind.Utc),
        EffectiveTo = r.EffectiveTo.HasValue ? DateTime.SpecifyKind(r.EffectiveTo.Value, DateTimeKind.Utc) : null,
        Status = r.Status,
        VersionNo = r.VersionNo,
        ApprovedBy = r.ApprovedBy,
        ApprovedAt = r.ApprovedAt.HasValue ? DateTime.SpecifyKind(r.ApprovedAt.Value, DateTimeKind.Utc) : null,
        CreatedBy = r.CreatedBy,
        CreatedAt = DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc),
        UpdatedAt = r.UpdatedAt.HasValue ? DateTime.SpecifyKind(r.UpdatedAt.Value, DateTimeKind.Utc) : null
    };
}
