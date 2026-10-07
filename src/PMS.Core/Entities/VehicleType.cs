using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class VehicleType
{
    public int Id { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; }

    public virtual ICollection<Slot> Slots { get; set; } = new List<Slot>();

    public virtual ICollection<Vehicle> Vehicles { get; set; } = new List<Vehicle>();
}
