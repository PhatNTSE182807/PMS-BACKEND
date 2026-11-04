using System.Threading.Tasks;

namespace PMS.Application.Services;

/// <summary>One price of one vehicle type at one lot, as told to customers.</summary>
public sealed record LotPriceNotice(
    int LotId,
    string LotName,
    string VehicleTypeName,
    string Summary,
    string PreviousSummary = null,
    string EventKey = null)
{
    private string Subject => string.IsNullOrWhiteSpace(VehicleTypeName) ? LotName : $"{LotName} ({VehicleTypeName})";

    /// <summary>Only a fee outside the headline price may have changed; "from X to X" would make no sense.</summary>
    private bool HeadlineChanged => !string.IsNullOrEmpty(PreviousSummary) && PreviousSummary != Summary;

    public string Message =>
        (HeadlineChanged ? $"Price for {Subject} changed from {PreviousSummary} to {Summary}." : $"Price for {Subject} is now {Summary}.")
        + " Vehicles already parked keep the price they entered with.";
}

/// <summary>
/// Tells customers about a price change. Always called after the change is saved, and never allowed to
/// break the Manager's request: implementations swallow their own failures.
/// </summary>
public interface ILotPriceNotifier
{
    /// <summary>The new price is in force now.</summary>
    Task PriceChangedAsync(LotPriceNotice notice);
}
