using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class SubscriptionPlan
{
    public int Id { get; set; }

    public int ParkingLotId { get; set; }

    public int VehicleTypeId { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public int DurationDays { get; set; }

    public decimal Price { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
