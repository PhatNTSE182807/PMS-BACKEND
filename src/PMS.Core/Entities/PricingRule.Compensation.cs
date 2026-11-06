namespace PMS.DataAccess.Persistence.Entities;

public partial class PricingRule
{
    /// <summary>
    /// Paid into the customer's XXIV wallet when a walk-in took their booked bay and no other bay of this vehicle type
    /// was free (BR-08, team addition). Not a price: changing it never notifies customers of a price change.
    /// </summary>
    public decimal BookedBayCompensation { get; set; }
}
