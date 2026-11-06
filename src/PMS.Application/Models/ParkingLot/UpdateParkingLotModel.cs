using System;

namespace PMS.Application.Models.ParkingLot;

public class UpdateParkingLotModel
{
    public string Name { get; set; }

    public string Address { get; set; }

    public string City { get; set; }

    public string District { get; set; }

    /// <summary>Map position of the lot entrance (WGS 84); both or neither. Null: the map finds the lot by address.</summary>
    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public TimeOnly? OpenTime { get; set; }

    public TimeOnly? CloseTime { get; set; }

    public bool Is24Hours { get; set; } = true;

    public string ImageUrl { get; set; }

    public string Description { get; set; }

    public string Phone { get; set; }

    public string ManagerUserId { get; set; }

    public string TimeZoneId { get; set; }
}
