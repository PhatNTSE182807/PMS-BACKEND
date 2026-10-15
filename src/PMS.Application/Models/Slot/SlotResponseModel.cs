using System;

namespace PMS.Application.Models.Slot;

public class SlotResponseModel
{
    public int Id { get; set; }

    public int ZoneId { get; set; }

    public string ZoneCode { get; set; }

    public string ZoneName { get; set; }

    public int ParkingLotId { get; set; }

    public string ParkingLotCode { get; set; }

    public int VehicleTypeId { get; set; }

    public string VehicleTypeCode { get; set; }

    public string VehicleTypeName { get; set; }

    public string Code { get; set; }

    public string Status { get; set; }

    public Guid? DedicatedVehicleId { get; set; }

    public string DedicatedNote { get; set; }

    public decimal? PosX { get; set; }

    public decimal? PosY { get; set; }

    public decimal? Width { get; set; }

    public decimal? Height { get; set; }

    public bool IsActive { get; set; }
}
