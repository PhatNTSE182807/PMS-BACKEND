using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PMS.Application.Models.Pricing;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>What a price update did to the charging fields of a rule.</summary>
public enum PriceUpdateOutcome
{
    /// <summary>The lot was empty: the new price is live.</summary>
    Applied,

    /// <summary>Vehicles are inside: the new price waits in a <see cref="PendingPriceChange"/>.</summary>
    Pending,

    /// <summary>The request asked for the price the rule already has.</summary>
    Unchanged
}

/// <summary>
/// The rules of the deferred price update: a Manager's new price goes live at once only when no vehicle is inside
/// the lot, otherwise it waits (one per rule, replaced by a newer request) and goes live when the last vehicle leaves.
/// The methods only stage changes on the context; the caller saves them together with the lot row inside
/// <see cref="LotConcurrency.RunAsync{T}"/>, which is what keeps the decision consistent with vehicles entering and leaving.
/// </summary>
public class DeferredPriceService(PmsDbContext db, TimeProvider clock, ILogger<DeferredPriceService> logger = null)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Decides what to do with <paramref name="proposed"/> for <paramref name="rule"/>, given the vehicles inside
    /// <paramref name="lot"/> (which must be the tracked, freshly read row). <paramref name="applyNow"/> skips the
    /// wait for rules that belong to no single lot, where "the lot is empty" has no meaning.
    /// </summary>
    public async Task<PriceUpdateOutcome> StageUpdateAsync(ParkingLot lot, PricingRule rule, PricingRulePrices proposed, string requestedBy, bool applyNow = false)
    {
        var current = PricingRulePrices.From(rule);
        var waiting = await db.PendingPriceChanges.FirstOrDefaultAsync(p => p.PricingRuleId == rule.Id);

        if (lot.OccupiedBays <= 0 || applyNow)
        {
            // Nobody is parked, so no stay can be affected: the price is live now and replaces any waiting change
            if (waiting != null)
                db.PendingPriceChanges.Remove(waiting);
            if (proposed == current)
                return PriceUpdateOutcome.Unchanged;

            proposed.ApplyTo(rule);
            return PriceUpdateOutcome.Applied;
        }

        if (proposed == current)
        {
            // Asking for today's price again withdraws the change that was waiting
            if (waiting != null)
                db.PendingPriceChanges.Remove(waiting);
            return PriceUpdateOutcome.Unchanged;
        }

        var json = JsonSerializer.Serialize(proposed);
        if (waiting == null)
        {
            db.PendingPriceChanges.Add(new PendingPriceChange
            {
                ParkingLotId = lot.Id,
                PricingRuleId = rule.Id,
                ProposedValuesJson = json,
                RevisionNo = 1,
                RequestedBy = requestedBy,
                RequestedAt = Now
            });
        }
        else
        {
            // Overwrite, not an error: the Manager changed their mind while the lot is still occupied
            waiting.ProposedValuesJson = json;
            waiting.RevisionNo++;
            waiting.RequestedBy = requestedBy;
            waiting.UpdatedAt = Now;
        }

        return PriceUpdateOutcome.Pending;
    }

    /// <summary>
    /// The lot just became empty: makes every waiting price of the lot live, removes the waiting rows and writes
    /// the history. Returns what to tell customers once the caller has saved.
    /// </summary>
    public async Task<IReadOnlyList<LotPriceNotice>> StagePromotionAsync(ParkingLot lot)
    {
        var waiting = await db.PendingPriceChanges.Where(p => p.ParkingLotId == lot.Id).OrderBy(p => p.Id).ToListAsync();
        if (waiting.Count == 0)
            return [];

        var ruleIds = waiting.Select(p => p.PricingRuleId).ToList();
        var rules = await db.PricingRules.Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id);
        var typeNames = await db.VehicleTypes.AsNoTracking().ToDictionaryAsync(v => v.Id, v => v.Name);

        var notices = new List<LotPriceNotice>();
        foreach (var change in waiting)
        {
            db.PendingPriceChanges.Remove(change);

            // A rule archived in the meantime has nothing left to change
            if (!rules.TryGetValue(change.PricingRuleId, out var rule) || rule.Status == "Archived")
                continue;

            PricingRulePrices prices;
            try
            {
                prices = JsonSerializer.Deserialize<PricingRulePrices>(change.ProposedValuesJson);
            }
            catch (JsonException ex)
            {
                // A damaged row must not keep the lot from going on; the Manager can set the price again
                logger?.LogError(ex, "Pending price change {ChangeId} of lot {LotId} could not be read and was dropped", change.Id, lot.Id);
                continue;
            }

            var before = PricingRulePrices.From(rule);
            prices.ApplyTo(rule);
            rule.VersionNo++;
            rule.UpdatedAt = Now;

            db.PricingRuleHistories.Add(new PricingRuleHistory
            {
                PricingRuleId = rule.Id,
                ChangeType = "PriceApplied",
                OldValuesJson = JsonSerializer.Serialize(before),
                NewValuesJson = change.ProposedValuesJson,
                ChangedBy = change.RequestedBy,
                ChangedAt = Now,
                Reason = "Applied when the lot became empty"
            });

            typeNames.TryGetValue(rule.VehicleTypeId, out var typeName);
            notices.Add(new LotPriceNotice(lot.Id, lot.Name, typeName, prices.Describe()));
        }

        return notices;
    }
}
