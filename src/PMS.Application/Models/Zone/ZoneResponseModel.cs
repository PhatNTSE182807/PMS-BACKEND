using System;

namespace PMS.Application.Models.Zone;

public class ZoneResponseModel
{
    public int Id { get; set; }

    public int ParkingLotId { get; set; }

    public string ParkingLotCode { get; set; }

    public string ParkingLotName { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }

    public string ZoneType { get; set; }

    public int? FloorNo { get; set; }

    public int Capacity { get; set; }

    public int? MaxReservationSlots { get; set; }

    public string LayoutJson { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public int TotalSlots { get; set; }
}
