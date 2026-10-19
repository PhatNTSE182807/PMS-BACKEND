using System;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// Whitelist / Blacklist plate (SRS FR-OPS-07, BR-16, BR-17, section 8 "PlateList"). A null lot means
/// the whole chain (Admin only); every entry needs a reason and an expiry date.
/// </summary>
public class PlateListEntry
{
    public int Id { get; set; }

    /// <summary>"Whitelist" or "Blacklist".</summary>
    public string ListType { get; set; }

    public string PlateNumber { get; set; }

    public string PlateNormalized { get; set; }

    public int? ParkingLotId { get; set; }

    public string Reason { get; set; }

    public DateTime ExpiresAt { get; set; }

    public bool IsActive { get; set; } = true;

    public string CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }
}
