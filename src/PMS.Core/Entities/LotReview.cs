using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class LotReview
{
    public int Id { get; set; }

    public int ParkingLotId { get; set; }

    public int CustomerId { get; set; }

    public long? SessionId { get; set; }

    public byte Rating { get; set; }

    public string Comment { get; set; }

    public bool IsVisible { get; set; }

    public DateTime CreatedAt { get; set; }
}
