using System.Threading.Tasks;

namespace PMS.Application.Services;

/// <summary>One price of one vehicle type at one lot, as told to customers.</summary>
public sealed record LotPriceNotice(
    int LotId,
    string LotName,
    int VehicleTypeId,
    string VehicleTypeName,
    string Summary,
    string PreviousSummary = null,
    string EventKey = null)
{
    private string Subject => string.IsNullOrWhiteSpace(VehicleTypeName) ? LotName : $"{LotName} ({VehicleTypeName})";

    /// <summary>Only a fee outside the headline price may have changed; "from X to X" would make no sense.</summary>
    private bool HeadlineChanged => !string.IsNullOrEmpty(PreviousSummary) && PreviousSummary != Summary;

    /// <summary>Title of the notification sent to customers with an upcoming booking for this vehicle type at the lot.</summary>
    public const string BookingTitle = "Parking Lot Price Update";

    /// <summary>Text of the notification sent to customers with an upcoming booking for this vehicle type at the lot.</summary>
    public string BookingMessage => $"The pricing rules for {LotName} have been updated. Please check your booking details.";

    /// <summary>"Motorbike price changed at Cong Hoa Parking Lot": the vehicle type first, so a customer sees at once whether it concerns them.</summary>
    public string Title => string.IsNullOrWhiteSpace(VehicleTypeName) ? $"Price changed at {LotName}" : $"{VehicleTypeName} price changed at {LotName}";

    /// <summary>The full price change, for the clients watching the lot and the customers without a booking there.</summary>
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
