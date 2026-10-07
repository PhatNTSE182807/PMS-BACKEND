using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class Device
{
    public int Id { get; set; }

    public int ParkingLotId { get; set; }

    public int? GateId { get; set; }

    public string DeviceType { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }

    public string Brand { get; set; }

    public string Model { get; set; }

    public string SerialNumber { get; set; }

    public string IpAddress { get; set; }

    public int? Port { get; set; }

    public string Protocol { get; set; }

    public string ConfigJson { get; set; }

    public string FirmwareVersion { get; set; }

    public string Status { get; set; }

    public DateTime? LastHeartbeatAt { get; set; }

    public DateOnly? InstalledAt { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual Gate Gate { get; set; }
}
