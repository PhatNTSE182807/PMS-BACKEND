using System.Collections.Generic;

namespace PMS.Application.Models.ParkingLot;

/// <summary>Real-time occupancy and performance data of one parking lot.</summary>
public class ParkingLotCapacityModel
{
    public string Id { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }

    public string Address { get; set; }

    public string ManagerOnDuty { get; set; }

    /// <summary>OPEN, FULL, MAINTENANCE, or CLOSED.</summary>
    public string Status { get; set; }

    public string LastUpdated { get; set; }

    public double OverallOccupancyPercentage { get; set; }

    public List<VehicleCapacityDetailModel> CapacityByVehicle { get; set; } = [];

    public List<ZoneCapacityDetailModel> Zones { get; set; } = [];

    public string LayoutTemplate { get; set; }

    public List<HourlyFlowModel> RecentHourlyFlow { get; set; } = [];
}

/// <summary>Capacity breakdown for a vehicle family (CAR, MOTORBIKE).</summary>
public class VehicleCapacityDetailModel
{
    public string VehicleType { get; set; }

    public int TotalSlots { get; set; }

    public int OccupiedSlots { get; set; }

    public int AvailableSlots { get; set; }

    public int MaintenanceSlots { get; set; }
}

/// <summary>Zone occupancy details for the modal dialog.</summary>
public class ZoneCapacityDetailModel
{
    public string ZoneId { get; set; }

    public string ZoneName { get; set; }

    public int TotalSlots { get; set; }

    public int OccupiedSlots { get; set; }

    /// <summary>AVAILABLE, WARNING, or FULL.</summary>
    public string Status { get; set; }
}

/// <summary>Recent hourly vehicle traffic flow (check-ins and check-outs).</summary>
public class HourlyFlowModel
{
    public string Hour { get; set; }

    public int CheckIns { get; set; }

    public int CheckOuts { get; set; }
}

