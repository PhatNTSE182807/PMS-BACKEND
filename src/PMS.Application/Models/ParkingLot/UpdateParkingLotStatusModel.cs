using System;

namespace PMS.Application.Models.ParkingLot;

public class UpdateParkingLotStatusModel
{
    public string Status { get; set; }

    public string ClosureReason { get; set; }

    public string MaintenanceNotice { get; set; }

    public DateTime? ClosedFrom { get; set; }

    public DateTime? ClosedUntil { get; set; }
}
