using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class Slot
{
    public int Id { get; set; }

    public int ZoneId { get; set; }

    public int VehicleTypeId { get; set; }

    public string Code { get; set; }

    public string Status { get; set; }

    public Guid? DedicatedVehicleId { get; set; }

    public string DedicatedNote { get; set; }

    public decimal? PosX { get; set; }

    public decimal? PosY { get; set; }

    public decimal? Width { get; set; }

    public decimal? Height { get; set; }

    public bool IsActive { get; set; }

    public byte[] RowVersion { get; set; }

    public virtual VehicleType VehicleType { get; set; }

    public virtual Zone Zone { get; set; }
}
