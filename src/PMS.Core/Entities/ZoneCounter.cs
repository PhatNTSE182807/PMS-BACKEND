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

    /// <summary>
    /// Fill alert last sent for the zone (Available, AlmostFull, Full; see ZoneFillLevel). Saved so a restart or a
    /// second API instance never sends the same alert twice.
    /// </summary>
    public string AlertLevel { get; set; } = "Available";

    /// <summary>When <see cref="AlertLevel"/> last changed (UTC); null until the first alert.</summary>
    public DateTime? AlertChangedAt { get; set; }

    public virtual Zone Zone { get; set; }
}
