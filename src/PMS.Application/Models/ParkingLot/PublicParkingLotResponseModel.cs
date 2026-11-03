using System;
using System.Collections.Generic;

namespace PMS.Application.Models.ParkingLot;

public class PublicParkingLotResponseModel
{
    public int Id { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }

    public string Address { get; set; }

    public string City { get; set; }

    public string District { get; set; }

    /// <summary>Map position of the lot; null until an Admin sets it (clients then locate the lot by address).</summary>
    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public TimeOnly? OpenTime { get; set; }

    public TimeOnly? CloseTime { get; set; }

    public bool Is24Hours { get; set; }

    public int Capacity { get; set; }

    public string LayoutTemplate { get; set; }

    public string ImageUrl { get; set; }

    public string Description { get; set; }

    public string Phone { get; set; }

    /// <summary>Active, or Maintenance (also when the lot has no Manager and Staff on duty yet).</summary>
    public string Status { get; set; }

    /// <summary>False while the lot is under maintenance: it can be viewed but not booked.</summary>
    public bool IsBookable { get; set; }

    /// <summary>Why the lot cannot be booked, in plain words; null when it can.</summary>
    public string MaintenanceNotice { get; set; }

    /// <summary>Bays in the lot's active zones: the zone counters, or the active slots of zones without one.</summary>
    public int TotalBays { get; set; }

    /// <summary>Bays free right now, from the same zone counters (live).</summary>
    public int FreeBays { get; set; }

    /// <summary>Free and total bays per vehicle family (CAR, MOTORBIKE). Counts only, never plates.</summary>
    public List<PublicLotBaysModel> BaysByVehicle { get; set; } = [];
}

/// <summary>Free and total bays of one vehicle family in a lot.</summary>
public class PublicLotBaysModel
{
    /// <summary>CAR (4- and 7-seat cars share bays) or MOTORBIKE.</summary>
    public string VehicleType { get; set; }

    public int TotalBays { get; set; }

    public int FreeBays { get; set; }
}
