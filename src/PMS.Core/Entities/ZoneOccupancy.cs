using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class ZoneOccupancy
{
    public int Id { get; set; }

    public int ZoneId { get; set; }

    public int VehicleTypeId { get; set; }

    public int TotalSlots { get; set; }

    public int OccupiedSlots { get; set; }

    public int ReservedSlots { get; set; }

    public int MaintenanceSlots { get; set; }

    public DateTime UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; }
}
