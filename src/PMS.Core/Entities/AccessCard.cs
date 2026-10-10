using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class AccessCard
{
    public int Id { get; set; }

    public string CardNumber { get; set; }

    public string CardType { get; set; }

    public int? ParkingLotId { get; set; }

    public int? CustomerId { get; set; }

    public Guid? VehicleId { get; set; }

    public string Status { get; set; }

    public DateTime? IssuedAt { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public string Note { get; set; }

    public DateTime CreatedAt { get; set; }
}
