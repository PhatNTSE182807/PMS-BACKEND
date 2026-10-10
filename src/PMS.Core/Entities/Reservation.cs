using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class Reservation
{
    public long Id { get; set; }

    public string ReservationCode { get; set; }

    public int CustomerId { get; set; }

    public Guid VehicleId { get; set; }

    public int ParkingLotId { get; set; }

    public int? ZoneId { get; set; }

    public int? SlotId { get; set; }

    public DateTime StartTime { get; set; }

    public DateTime EndTime { get; set; }

    public decimal DepositAmount { get; set; }

    public string Status { get; set; }

    public DateTime? CancelledAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
