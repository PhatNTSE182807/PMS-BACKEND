using System;

namespace PMS.Application.Models.ParkingLot;

public class UpdateParkingLotModel
{
    public string Name { get; set; }

    public string Address { get; set; }

    public string City { get; set; }

    public string District { get; set; }

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public TimeOnly? OpenTime { get; set; }

    public TimeOnly? CloseTime { get; set; }

    public bool Is24Hours { get; set; } = true;

    public int Capacity { get; set; }

    public string ImageUrl { get; set; }

    public string Description { get; set; }

    public string Phone { get; set; }

    public string ManagerUserId { get; set; }

    public string TimeZoneId { get; set; }
}
