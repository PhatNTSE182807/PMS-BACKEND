using System.Threading.Tasks;

namespace PMS.Application.Services;

/// <summary>One price of one vehicle type at one lot, as told to customers.</summary>
public sealed record LotPriceNotice(int LotId, string LotName, string VehicleTypeName, string Summary)
{
    private string Subject => string.IsNullOrWhiteSpace(VehicleTypeName) ? LotName : $"{LotName} ({VehicleTypeName})";

    public string AppliedMessage => $"Price for {Subject} is now {Summary}.";

    public string ScheduledMessage => $"Price for {Subject} will be updated to {Summary} once the lot is empty.";
}

/// <summary>
/// Tells customers about a price change. Always called after the change is saved, and never allowed to
/// break the Manager's or the gate's request: implementations swallow their own failures.
/// </summary>
public interface ILotPriceNotifier
{
    /// <summary>The new price is in force now.</summary>
    Task PriceAppliedAsync(LotPriceNotice notice);

    /// <summary>The lot still has vehicles inside: the new price will start once it is empty.</summary>
    Task PriceScheduledAsync(LotPriceNotice notice);
}
