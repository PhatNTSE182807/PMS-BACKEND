using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class Gate
{
    public int Id { get; set; }

    public int ParkingLotId { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }

    public string Direction { get; set; }

    public int? ZoneId { get; set; }

    public bool IsActive { get; set; }

    public virtual ICollection<Device> Devices { get; set; } = new List<Device>();

    public virtual ParkingLot ParkingLot { get; set; }

    public virtual ICollection<ParkingSession> ParkingSessionEntryGates { get; set; } = new List<ParkingSession>();

    public virtual ICollection<ParkingSession> ParkingSessionExitGates { get; set; } = new List<ParkingSession>();
}
