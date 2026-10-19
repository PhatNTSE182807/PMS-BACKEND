using System;

namespace PMS.Application.Models.Slot;

public class UpdateSlotModel
{
    public int? VehicleTypeId { get; set; }

    public string Code { get; set; }

    public Guid? DedicatedVehicleId { get; set; }

    public string DedicatedNote { get; set; }

    public bool? IsActive { get; set; }
}
