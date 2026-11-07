namespace PMS.DataAccess.Persistence.Entities;

public partial class PricingRule
{
    /// <summary>
    /// Paid into the customer's XXIV wallet when a walk-in took their booked bay and no other bay of this vehicle type
    /// was free (BR-08, team addition). Not a price: changing it never notifies customers of a price change.
    /// </summary>
    public decimal BookedBayCompensation { get; set; }

    /// <summary>Default compensation for a car rule (4- or 7-seat), in VND; the Manager can change it per rule.</summary>
    public const decimal DefaultCarCompensation = 100_000;

    /// <summary>Default compensation for a motorbike rule, in VND; the Manager can change it per rule.</summary>
    public const decimal DefaultMotorbikeCompensation = 50_000;

    /// <summary>The default compensation for a vehicle type code: cars 100,000 VND, motorbikes 50,000 VND, others 0.</summary>
    public static decimal DefaultCompensationFor(string vehicleTypeCode) =>
        vehicleTypeCode?.StartsWith("CAR", System.StringComparison.OrdinalIgnoreCase) == true ? DefaultCarCompensation
        : vehicleTypeCode == "MOTORBIKE" ? DefaultMotorbikeCompensation
        : 0;
}
