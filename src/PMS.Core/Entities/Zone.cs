using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class Zone
{
    public int Id { get; set; }

    public int ParkingLotId { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }

    public string ZoneType { get; set; }

    public int? FloorNo { get; set; }

    public int Capacity { get; set; }

    public string LayoutJson { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual ParkingLot ParkingLot { get; set; }

    public virtual ICollection<Slot> Slots { get; set; } = new List<Slot>();
}
