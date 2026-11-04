namespace PMS.Application.Models.Layout;

/* ------------------------------------------------------------------ */
/* Layouts are generated from a ready-made model (LotTemplates), never */
/* drawn by hand. Every floor is a grid of rows x cols; each slot takes */
/* one cell, which both the 3D renderer and the 2D fallback draw from.  */
/* ------------------------------------------------------------------ */

public class LayoutFloorModel
{
    /// <summary>0 = ground level.</summary>
    public int FloorNo { get; set; }

    public string Name { get; set; }

    /// <summary>car, motorbike or mixed.</summary>
    public string Kind { get; set; } = "car";

    public int Rows { get; set; }

    public int Cols { get; set; }
}

/* ------------------------------------------------------------------ */
/* Published 3D layout                                                 */
/* ------------------------------------------------------------------ */

public class LayoutResponseModel
{
    public int ParkingLotId { get; set; }

    public string ParkingLotName { get; set; }

    /// <summary>Lot lifecycle status, so clients can show "Temporarily closed".</summary>
    public string LotStatus { get; set; }

    /// <summary>Changes when the layout is regenerated; clients cache the layout by it.</summary>
    public int Version { get; set; }

    /// <summary>The ready-made model (L3S20, L3S50, L4S30) the 3D renderer draws.</summary>
    public string TemplateId { get; set; }

    public DateTime? PublishedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<LayoutFloorModel> Floors { get; set; } = [];

    public List<LayoutZoneModel> Zones { get; set; } = [];

    public List<LayoutSlotModel> Slots { get; set; } = [];
}

public class LayoutZoneModel
{
    public int ZoneId { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }

    public string ZoneType { get; set; }

    public int? FloorNo { get; set; }
}

/// <summary>A placed slot: geometry plus the business data needed to colour and pick it. Never a plate.</summary>
public class LayoutSlotModel
{
    public int SlotId { get; set; }

    public string Code { get; set; }

    public int ZoneId { get; set; }

    public string VehicleTypeCode { get; set; }

    public int FloorNo { get; set; }

    public int Row { get; set; }

    public int Col { get; set; }

    public int Rotation { get; set; }

    public string ModelUrl { get; set; }
}

/* ------------------------------------------------------------------ */
/* Live status channel (FR-3D-02, FR-3D-08): statuses and free counts  */
/* ------------------------------------------------------------------ */

public class LayoutStatusResponseModel
{
    public int ParkingLotId { get; set; }

    /// <summary>Version of the published layout these statuses belong to.</summary>
    public int Version { get; set; }

    public DateTime GeneratedAt { get; set; }

    public List<SlotStatusModel> Slots { get; set; } = [];

    public List<ZoneAvailabilityModel> Zones { get; set; } = [];
}

public class SlotStatusModel
{
    public int SlotId { get; set; }

    /// <summary>Available, Occupied, Reserved or Maintenance.</summary>
    public string Status { get; set; }
}

public class ZoneAvailabilityModel
{
    public int ZoneId { get; set; }

    public int Total { get; set; }

    public int Free { get; set; }

    /// <summary>
    /// The vehicles counted in this zone without a bay of their own, oldest entry first.
    /// Only the 2D plan with detail = slots fills it.
    /// </summary>
    public List<ParkedVehicleModel> WalkIns { get; set; } = [];
}

/// <summary>What the map draws of a parked vehicle. Never a plate.</summary>
public class ParkedVehicleModel
{
    /// <summary>Registered colour; null for a vehicle that is not registered.</summary>
    public string VehicleColor { get; set; }

    /// <summary>CAR_4, CAR_7 or MOTORBIKE; null when unknown.</summary>
    public string VehicleTypeCode { get; set; }
}

/* ------------------------------------------------------------------ */
/* 2D fallback (FR-3D-06): same grid, with a level of detail           */
/* ------------------------------------------------------------------ */

public static class LayoutDetail
{
    /// <summary>Every slot as a cell, with its status.</summary>
    public const string Slots = "slots";

    /// <summary>Low detail: one block per zone and floor with free / total counts.</summary>
    public const string Zones = "zones";
}

public class Layout2DResponseModel
{
    public int ParkingLotId { get; set; }

    public string ParkingLotName { get; set; }

    public string LotStatus { get; set; }

    public int Version { get; set; }

    /// <summary>slots or zones.</summary>
    public string Detail { get; set; }

    public List<Layout2DFloorModel> Floors { get; set; } = [];

    /// <summary>Free and total spaces of each active zone, from its live counter (walk-ins have no bay of their own).</summary>
    public List<ZoneAvailabilityModel> ZoneCounters { get; set; } = [];
}

public class Layout2DFloorModel
{
    public int FloorNo { get; set; }

    public string Name { get; set; }

    public string Kind { get; set; }

    public int Rows { get; set; }

    public int Cols { get; set; }

    public int Total { get; set; }

    public int Free { get; set; }

    /// <summary>Filled when detail = slots.</summary>
    public List<Layout2DCellModel> Cells { get; set; } = [];

    /// <summary>Filled when detail = zones.</summary>
    public List<Layout2DZoneBlockModel> ZoneBlocks { get; set; } = [];
}

public class Layout2DCellModel
{
    public int SlotId { get; set; }

    public string Code { get; set; }

    public int ZoneId { get; set; }

    public string VehicleTypeCode { get; set; }

    public int Row { get; set; }

    public int Col { get; set; }

    public int Rotation { get; set; }

    public string Status { get; set; }

    /// <summary>Registered colour of the vehicle parked here; null when none is known. Never a plate.</summary>
    public string VehicleColor { get; set; }

    /// <summary>Type of the vehicle parked here, which a car bay does not fix (CAR_4 or CAR_7); null when none is known.</summary>
    public string ParkedVehicleTypeCode { get; set; }
}

/// <summary>The cells a zone covers on one floor, as a bounding box of rows and columns.</summary>
public class Layout2DZoneBlockModel
{
    public int ZoneId { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }

    public int MinRow { get; set; }

    public int MaxRow { get; set; }

    public int MinCol { get; set; }

    public int MaxCol { get; set; }

    public int Total { get; set; }

    public int Free { get; set; }
}
