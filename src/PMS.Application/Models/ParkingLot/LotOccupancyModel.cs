using System.Collections.Generic;

namespace PMS.Application.Models.ParkingLot;

/// <summary>Live occupancy of one lot from its zone counters (walk-ins and bookings alike).</summary>
public class LotOccupancyModel
{
    public int ParkingLotId { get; set; }

    public int TotalBays { get; set; }

    public int OccupiedBays { get; set; }

    public int FreeBays { get; set; }

    /// <summary>Occupied bays over total bays, 0-100, one decimal.</summary>
    public decimal OccupancyRate { get; set; }

    /// <summary>Free and total bays per vehicle family (CAR, MOTORBIKE).</summary>
    public List<PublicLotBaysModel> BaysByVehicle { get; set; } = [];

    public List<LotZoneOccupancyModel> Zones { get; set; } = [];
}

/// <summary>One active zone of a lot and its live counter.</summary>
public class LotZoneOccupancyModel
{
    public int ZoneId { get; set; }

    public string ZoneName { get; set; }

    /// <summary>CAR or MOTORBIKE, from the zone's bays; null when it has none.</summary>
    public string VehicleType { get; set; }

    public int TotalBays { get; set; }

    public int OccupiedBays { get; set; }

    public int FreeBays { get; set; }
}
