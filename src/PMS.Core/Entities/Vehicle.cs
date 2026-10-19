using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class Vehicle
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int? CustomerId { get; set; }

    public int VehicleTypeId { get; set; }

    public string PlateNumber { get; set; }

    public string PlateNormalized { get; set; }

    public string Brand { get; set; }

    public string Model { get; set; }

    public string Color { get; set; }

    public bool IsDefault { get; set; }

    /// <summary>Citizen ID (CCCD) of the registered owner on the vehicle papers.</summary>
    public string CitizenId { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual Customer Customer { get; set; }

    public virtual ICollection<ParkingSession> ParkingSessions { get; set; } = new List<ParkingSession>();

    public virtual VehicleType VehicleType { get; set; }
}
