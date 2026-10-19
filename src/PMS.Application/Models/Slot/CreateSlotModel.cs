using System;

namespace PMS.Application.Models.Slot;

public class CreateSlotModel
{
    public int ZoneId { get; set; }

    public int VehicleTypeId { get; set; }

    public string Code { get; set; }

    public string Status { get; set; } = "Available";

    public Guid? DedicatedVehicleId { get; set; }

    public string DedicatedNote { get; set; }
}
