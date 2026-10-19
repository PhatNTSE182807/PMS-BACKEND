namespace PMS.Application.Models.Layout;

/// <summary>A ready-made parking building the Admin picks when registering a lot (GET /api/lot-templates).</summary>
public class LotTemplateResponseModel
{
    /// <summary>L3S20, L3S50 or L4S30; the same ids the frontend 3D renderer knows.</summary>
    public string Id { get; set; }

    public string Name { get; set; }

    public string Summary { get; set; }

    public int SlotsPerFloor { get; set; }

    public int CarSlots { get; set; }

    public int MotorbikeSlots { get; set; }

    public int TotalSlots { get; set; }

    public List<LayoutFloorModel> Floors { get; set; } = [];
}

/// <summary>Admin applies a template to a lot that has no zones yet (lots registered before templates).</summary>
public class ApplyLotTemplateModel
{
    public string LayoutTemplate { get; set; }
}
