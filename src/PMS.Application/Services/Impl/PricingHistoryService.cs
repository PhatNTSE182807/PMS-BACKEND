using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.Pricing;
using PMS.DataAccess.Persistence;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Turns the stored history rows into readable entries. Old rows were written in different shapes (full rule
/// models, a few fields, or only a status), so fields are matched by name, ignoring case, and only the fields the
/// pricing page shows are compared.
/// </summary>
public partial class PricingHistoryService(PmsDbContext pmsContext, DatabaseContext identityContext, LotScopeService lotScopeService) : IPricingHistoryService
{
    /// <summary>The fields a Manager can change on the pricing page, in display order.</summary>
    private static readonly string[] Fields =
    [
        "name", "chargeModel", "entryFee", "firstBlockMinutes", "firstBlockPrice", "blockMinutes", "blockPrice",
        "dayStartTime", "nightStartTime", "dayPrice", "nightPrice", "maxDailyFee", "lostTicketFee",
        "bookedBayCompensation", "penaltyFee", "status"
    ];

    public async Task<IReadOnlyList<PricingRuleHistoryModel>> GetHistoryAsync(int lotId, int? ruleId = null, int take = 100)
    {
        var lotExists = await pmsContext.ParkingLots.AsNoTracking().AnyAsync(p => p.Id == lotId && !p.IsDeleted);
        if (!lotExists)
            throw new NotFoundException($"Parking lot with ID {lotId} not found.");
        // The lot's Manager, or any Admin
        await lotScopeService.EnsureCanManageAsync(lotId);

        take = Math.Clamp(take, 1, 500);
        var rules = await pmsContext.PricingRules.AsNoTracking()
            .Where(r => r.ParkingLotId == lotId || r.ParkingLotId == null)
            .Select(r => new { r.Id, r.Name, r.VehicleTypeId })
            .ToDictionaryAsync(r => r.Id);
        var ruleIds = rules.Keys.Where(id => ruleId == null || id == ruleId).ToList();

        var rows = await pmsContext.PricingRuleHistories.AsNoTracking()
            .Where(h => ruleIds.Contains(h.PricingRuleId))
            .OrderByDescending(h => h.ChangedAt).ThenByDescending(h => h.Id)
            .Take(take)
            .ToListAsync();

        var types = await pmsContext.VehicleTypes.AsNoTracking().ToDictionaryAsync(v => v.Id, v => new { v.Code, v.Name });
        var userIds = rows.Select(r => r.ChangedBy).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        var names = await pmsContext.Employees.AsNoTracking()
            .Where(e => userIds.Contains(e.UserId))
            .GroupBy(e => e.UserId)
            .Select(g => new { UserId = g.Key, FullName = g.Select(e => e.FullName).FirstOrDefault() })
            .ToDictionaryAsync(e => e.UserId, e => e.FullName);
        // Admins have no employee row: their account name and role come from the identity store
        var accounts = await identityContext.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.UserName, u.Role })
            .ToDictionaryAsync(u => u.Id);

        return rows.Select(h =>
        {
            var rule = rules[h.PricingRuleId];
            types.TryGetValue(rule.VehicleTypeId, out var type);
            var oldValues = Parse(h.OldValuesJson);
            var newValues = Parse(h.NewValuesJson);
            var account = h.ChangedBy != null && accounts.TryGetValue(h.ChangedBy, out var a) ? a : null;
            return new PricingRuleHistoryModel
            {
                Id = h.Id,
                PricingRuleId = h.PricingRuleId,
                // The name the rule had then when the row says so, else its name now
                RuleName = Text(newValues, "name") ?? Text(oldValues, "name") ?? rule.Name,
                VehicleTypeCode = type?.Code,
                VehicleTypeName = type?.Name,
                ChangeType = h.ChangeType,
                VersionNo = Number(newValues, "versionNo"),
                DraftId = DraftId(h.Reason),
                ChangedBy = h.ChangedBy,
                ChangedByName = (h.ChangedBy != null && names.TryGetValue(h.ChangedBy, out var name) ? name : null) ?? account?.UserName,
                ChangedByRole = account?.Role,
                ChangedAt = DateTime.SpecifyKind(h.ChangedAt, DateTimeKind.Utc),
                Reason = h.Reason,
                Changes = Diff(h.ChangeType, oldValues, newValues)
            };
        }).ToList();
    }

    private static List<PricingFieldChangeModel> Diff(string changeType, Dictionary<string, JsonElement> oldValues, Dictionary<string, JsonElement> newValues)
    {
        var changes = new List<PricingFieldChangeModel>();
        foreach (var field in Fields)
        {
            var hasOld = oldValues.TryGetValue(field, out var before);
            var hasNew = newValues.TryGetValue(field, out var after);
            // A created rule lists its starting values
            if (changeType == PricingHistoryChangeTypes.Create)
            {
                if (hasNew && after.ValueKind != JsonValueKind.Null && field != "status")
                    changes.Add(new PricingFieldChangeModel { Field = field, NewValue = after });
                continue;
            }
            // Elsewhere a field only one side recorded tells nothing about a change
            if (!hasOld || !hasNew || Same(before, after))
                continue;
            changes.Add(new PricingFieldChangeModel { Field = field, OldValue = before, NewValue = after });
        }
        return changes;
    }

    private static bool Same(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
            return a.GetDecimal() == b.GetDecimal();
        if (a.ValueKind == JsonValueKind.String && b.ValueKind == JsonValueKind.String)
            return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
        return a.ValueKind == b.ValueKind && a.GetRawText() == b.GetRawText();
    }

    /// <summary>The top-level fields of a stored JSON object, by name ignoring case; empty for null or non-objects.</summary>
    private static Dictionary<string, JsonElement> Parse(string json)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
            return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return result;
            foreach (var property in doc.RootElement.EnumerateObject())
                result[property.Name] = property.Value.Clone();
        }
        catch (JsonException)
        {
            // A row that is not valid JSON still shows its type, time and reason
        }
        return result;
    }

    private static string Text(Dictionary<string, JsonElement> values, string field) =>
        values.TryGetValue(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Number(Dictionary<string, JsonElement> values, string field) =>
        values.TryGetValue(field, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    /// <summary>Draft rows and applied drafts name their draft in the reason ("... draft #12 ...").</summary>
    private static int? DraftId(string reason)
    {
        if (string.IsNullOrEmpty(reason))
            return null;
        var match = DraftPattern().Match(reason);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    [GeneratedRegex(@"draft #(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DraftPattern();
}
