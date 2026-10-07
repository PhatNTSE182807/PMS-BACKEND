using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class ZoneVehicleCapacity
{
    public int Id { get; set; }

    public int ZoneId { get; set; }

    public int VehicleTypeId { get; set; }

    public int SlotCount { get; set; }
}
