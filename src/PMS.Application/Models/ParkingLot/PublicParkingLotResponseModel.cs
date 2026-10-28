using System;

namespace PMS.Application.Models.ParkingLot;

public class PublicParkingLotResponseModel
{
    public int Id { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }

    public string Address { get; set; }

    public string City { get; set; }

    public string District { get; set; }

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
}
