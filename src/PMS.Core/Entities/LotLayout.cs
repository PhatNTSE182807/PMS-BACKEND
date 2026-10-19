using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// Layout 3D of a parking lot (SRS 8 "Layout3D"): display only, never the source of slots.
/// A lot has at most one Draft and one Published version; older versions are Archived.
/// </summary>
public partial class LotLayout
{
    public int Id { get; set; }

    public int ParkingLotId { get; set; }

    public int Version { get; set; }

    /// <summary>Draft, Published or Archived.</summary>
    public string Status { get; set; }

    /// <summary>Optional frontend building template (e.g. L3S20) the renderer starts from.</summary>
    public string TemplateId { get; set; }

    /// <summary>Floors as JSON: [{ floorNo, name, kind, rows, cols }].</summary>
    public string FloorsJson { get; set; }

    public DateTime CreatedAt { get; set; }

    public string CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public DateTime? PublishedAt { get; set; }

    public string PublishedBy { get; set; }

    public virtual ICollection<LotLayoutSlot> Slots { get; set; } = new List<LotLayoutSlot>();
}

/// <summary>
/// Where a business slot sits in a layout (SRS 8 "Slot3DMeta"): floor, grid cell, heading and GLB model.
/// It points to a Slot and never creates or removes one.
/// </summary>
public partial class LotLayoutSlot
{
    public int Id { get; set; }

    public int LayoutId { get; set; }

    public int SlotId { get; set; }

    public int FloorNo { get; set; }

    public int Row { get; set; }

    public int Col { get; set; }

    /// <summary>Heading in degrees: 0, 90, 180 or 270.</summary>
    public int Rotation { get; set; }

    /// <summary>Optional GLB/glTF model; the renderer uses its default for the vehicle type otherwise.</summary>
    public string ModelUrl { get; set; }

    public virtual LotLayout Layout { get; set; }
}
