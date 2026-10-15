using System;

namespace PMS.DataAccess.Persistence.Entities;

public partial class ZoneCounter
{
    public int Id { get; set; }

    public int ZoneId { get; set; }

    public int AvailableSpaces { get; set; }

    public int OccupiedSpaces { get; set; }

    public int TotalCapacity { get; set; }

    public DateTime LastUpdatedAt { get; set; }

    public virtual Zone Zone { get; set; }
}
