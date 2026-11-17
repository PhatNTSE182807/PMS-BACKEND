using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PMS.Application.Models.Pricing;

/// <summary>The kinds of pricing history rows (<c>PricingRuleHistories.ChangeType</c>, at most 20 characters).</summary>
public static class PricingHistoryChangeTypes
{
    public const string Create = "Create";
    public const string Update = "Update";
    public const string StatusChange = "StatusChange";
    /// <summary>The rule was archived.</summary>
    public const string Delete = "Delete";
    /// <summary>A price change was saved as a draft (nothing changed at the gates).</summary>
    public const string DraftSaved = "DraftSaved";
    /// <summary>A saved draft was edited.</summary>
    public const string DraftUpdated = "DraftUpdated";
    /// <summary>A draft was dropped without being applied.</summary>
    public const string DraftDiscarded = "DraftDiscarded";
}

/// <summary>One entry of a lot's pricing history: who changed which rule, when, and what changed.</summary>
public class PricingRuleHistoryModel
{
    public long Id { get; set; }

    public int PricingRuleId { get; set; }

    public string RuleName { get; set; }

    public string VehicleTypeCode { get; set; }

    public string VehicleTypeName { get; set; }

    /// <summary>One of <see cref="PricingHistoryChangeTypes"/>.</summary>
    public string ChangeType { get; set; }

    /// <summary>The rule version after the change, when the row records it.</summary>
    public int? VersionNo { get; set; }

    /// <summary>The draft the row is about (draft rows and applied drafts), when known.</summary>
    public int? DraftId { get; set; }

    public string ChangedBy { get; set; }

    public string ChangedByName { get; set; }

    /// <summary>"Admin" or "Manager" when the account is known.</summary>
    public string ChangedByRole { get; set; }

    public DateTime ChangedAt { get; set; }

    public string Reason { get; set; }

    /// <summary>
    /// The fields that changed. For a created rule, its starting values (old value null); for a draft, what it
    /// proposes compared with the live prices at that time.
    /// </summary>
    public IReadOnlyList<PricingFieldChangeModel> Changes { get; set; } = [];
}

/// <summary>One changed field. Values are the raw JSON values (numbers, "HH:mm:ss" times, text, or null).</summary>
public class PricingFieldChangeModel
{
    /// <summary>camelCase field name of the rule, e.g. "blockPrice", "maxDailyFee", "status".</summary>
    public string Field { get; set; }

    public JsonElement? OldValue { get; set; }

    public JsonElement? NewValue { get; set; }
}
