using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class Subscription
{
    public long Id { get; set; }

    public string SubscriptionCode { get; set; }

    public int CustomerId { get; set; }

    public int VehicleId { get; set; }

    public int PlanId { get; set; }

    public int ParkingLotId { get; set; }

    public int? AccessCardId { get; set; }

    public DateOnly StartDate { get; set; }

    public DateOnly EndDate { get; set; }

    public bool AutoRenew { get; set; }

    public string Status { get; set; }

    public DateTime? CancelledAt { get; set; }

    public string CancelReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
