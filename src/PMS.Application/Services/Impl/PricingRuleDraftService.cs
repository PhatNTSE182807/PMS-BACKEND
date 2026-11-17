using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.Pricing;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Stores price changes as drafts (dbo.PricingRuleDrafts) and applies them through
/// <see cref="IPricingRuleService.UpdatePricingRuleAsync"/>, so an applied draft behaves exactly like a direct save.
/// </summary>
public class PricingRuleDraftService(
    PmsDbContext pmsContext,
    IPricingRuleService pricingRuleService,
    LotScopeService lotScopeService,
    TimeProvider clock) : IPricingRuleDraftService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PricingRuleDraftModel>> GetDraftsAsync(int lotId)
    {
        // Checks the lot and the caller's scope; archived rules are left out, and so are their drafts
        var rules = await pricingRuleService.GetPricingRulesAsync(lotId);
        var versions = rules.ToDictionary(r => r.Id, r => r.VersionNo);
        var ruleIds = versions.Keys.ToList();

        var drafts = await pmsContext.PricingRuleDrafts.AsNoTracking()
            .Where(d => d.ParkingLotId == lotId && d.Status == PricingRuleDraftStatuses.Open && ruleIds.Contains(d.PricingRuleId))
            .OrderByDescending(d => d.UpdatedAt ?? d.CreatedAt)
            .ToListAsync();

        var names = await NamesAsync(drafts.Select(d => d.CreatedBy));
        return drafts.Select(d => ToModel(d, versions[d.PricingRuleId], names)).ToList();
    }

    public async Task<PricingRuleDraftModel> CreateDraftAsync(int lotId, int ruleId, SavePricingRuleDraftModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var rule = await EditableRuleAsync(lotId, ruleId);
        var now = clock.GetUtcNow().UtcDateTime;

        var draft = new PricingRuleDraft
        {
            PricingRuleId = rule.Id,
            ParkingLotId = lotId,
            ValuesJson = JsonSerializer.Serialize(model.Values, Json),
            Note = Clean(model.Note),
            BaseVersionNo = rule.VersionNo,
            Status = PricingRuleDraftStatuses.Open,
            CreatedBy = lotScopeService.UserId,
            CreatedAt = now
        };
        pmsContext.PricingRuleDrafts.Add(draft);
        await pmsContext.SaveChangesAsync();

        return ToModel(draft, rule.VersionNo, await NamesAsync([draft.CreatedBy]));
    }

    public async Task<PricingRuleDraftModel> UpdateDraftAsync(int lotId, int ruleId, int draftId, SavePricingRuleDraftModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var rule = await EditableRuleAsync(lotId, ruleId);
        var draft = await OpenDraftAsync(lotId, ruleId, draftId);

        draft.ValuesJson = JsonSerializer.Serialize(model.Values, Json);
        draft.Note = Clean(model.Note);
        // The Manager saw the live prices again while editing: the draft is now based on the current version
        draft.BaseVersionNo = rule.VersionNo;
        draft.UpdatedBy = lotScopeService.UserId;
        draft.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await pmsContext.SaveChangesAsync();

        return ToModel(draft, rule.VersionNo, await NamesAsync([draft.CreatedBy]));
    }

    public async Task<PricingRuleModel> ApplyDraftAsync(int lotId, int ruleId, int draftId)
    {
        var rule = await EditableRuleAsync(lotId, ruleId);
        var draft = await OpenDraftAsync(lotId, ruleId, draftId);
        var values = JsonSerializer.Deserialize<UpdatePricingRuleModel>(draft.ValuesJson, Json)
            ?? throw new BadRequestException("This draft has no prices to apply.");

        // Only the prices come from the draft. Lifecycle and fields the pricing page does not edit stay as the
        // rule has them now, so an old draft never switches a rule back on or off.
        var update = new UpdatePricingRuleModel
        {
            Name = values.Name,
            ChargeModel = values.ChargeModel,
            EntryFee = values.EntryFee,
            FirstBlockMinutes = values.FirstBlockMinutes,
            FirstBlockPrice = values.FirstBlockPrice,
            BlockMinutes = values.BlockMinutes,
            BlockPrice = values.BlockPrice,
            DayStartTime = values.DayStartTime,
            NightStartTime = values.NightStartTime,
            DayPrice = values.DayPrice,
            NightPrice = values.NightPrice,
            MaxDailyFee = values.MaxDailyFee,
            LostTicketFee = values.LostTicketFee,
            BookedBayCompensation = values.BookedBayCompensation,
            PenaltyFee = values.PenaltyFee,
            GraceMinutes = rule.GraceMinutes,
            OvernightSurcharge = rule.OvernightSurcharge,
            OverstayAfterHours = rule.OverstayAfterHours,
            OverstaySurchargePerDay = rule.OverstaySurchargePerDay,
            NoShowFee = rule.NoShowFee,
            ZoneId = rule.ZoneId,
            VehicleTypeId = rule.VehicleTypeId,
            Priority = rule.Priority,
            EffectiveFrom = rule.EffectiveFrom,
            EffectiveTo = rule.EffectiveTo,
            Status = rule.Status,
            Reason = string.IsNullOrWhiteSpace(draft.Note) ? $"Applied price draft #{draft.Id}" : $"Applied price draft #{draft.Id}: {draft.Note}"
        };

        var updated = await pricingRuleService.UpdatePricingRuleAsync(lotId, ruleId, update);

        draft.Status = PricingRuleDraftStatuses.Applied;
        draft.ClosedBy = lotScopeService.UserId;
        draft.ClosedAt = clock.GetUtcNow().UtcDateTime;
        await pmsContext.SaveChangesAsync();

        return updated;
    }

    public async Task DiscardDraftAsync(int lotId, int ruleId, int draftId)
    {
        // Same scope check as the other calls; discarding works on any rule the caller can see
        await pricingRuleService.GetPricingRuleByIdAsync(lotId, ruleId);
        var draft = await OpenDraftAsync(lotId, ruleId, draftId);

        draft.Status = PricingRuleDraftStatuses.Discarded;
        draft.ClosedBy = lotScopeService.UserId;
        draft.ClosedAt = clock.GetUtcNow().UtcDateTime;
        await pmsContext.SaveChangesAsync();
    }

    /// <summary>The rule, after the lot and scope checks; drafts are only kept for rules the caller may change.</summary>
    private async Task<PricingRuleModel> EditableRuleAsync(int lotId, int ruleId)
    {
        var rule = await pricingRuleService.GetPricingRuleByIdAsync(lotId, ruleId);
        if (string.Equals(rule.Status, "Archived", StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException("This pricing rule is archived: its prices can no longer change.");
        if (rule.ParkingLotId == null && lotScopeService.Role != "Admin")
            throw new BadRequestException("The shared pricing rule of the whole system cannot be changed here.");
        return rule;
    }

    private async Task<PricingRuleDraft> OpenDraftAsync(int lotId, int ruleId, int draftId)
    {
        var draft = await pmsContext.PricingRuleDrafts
            .FirstOrDefaultAsync(d => d.Id == draftId && d.PricingRuleId == ruleId && d.ParkingLotId == lotId)
            ?? throw new NotFoundException($"Price draft {draftId} was not found for this pricing rule.");
        if (draft.Status != PricingRuleDraftStatuses.Open)
            throw new BadRequestException(draft.Status == PricingRuleDraftStatuses.Applied
                ? "This draft has already been applied."
                : "This draft has been discarded.");
        return draft;
    }

    private async Task<Dictionary<string, string>> NamesAsync(IEnumerable<string> userIds)
    {
        var ids = userIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        if (ids.Count == 0)
            return [];
        return await pmsContext.Employees.AsNoTracking()
            .Where(e => ids.Contains(e.UserId))
            .GroupBy(e => e.UserId)
            .Select(g => new { UserId = g.Key, FullName = g.Select(e => e.FullName).FirstOrDefault() })
            .ToDictionaryAsync(e => e.UserId, e => e.FullName);
    }

    private static string Clean(string note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    private static PricingRuleDraftModel ToModel(PricingRuleDraft d, int currentVersion, IReadOnlyDictionary<string, string> names) => new()
    {
        Id = d.Id,
        PricingRuleId = d.PricingRuleId,
        Values = JsonSerializer.Deserialize<UpdatePricingRuleModel>(d.ValuesJson, Json),
        Note = d.Note,
        BaseVersionNo = d.BaseVersionNo,
        CurrentVersionNo = currentVersion,
        Status = d.Status,
        CreatedBy = d.CreatedBy,
        CreatedByName = d.CreatedBy != null && names.TryGetValue(d.CreatedBy, out var name) ? name : null,
        CreatedAt = DateTime.SpecifyKind(d.CreatedAt, DateTimeKind.Utc),
        UpdatedAt = d.UpdatedAt.HasValue ? DateTime.SpecifyKind(d.UpdatedAt.Value, DateTimeKind.Utc) : null
    };
}
