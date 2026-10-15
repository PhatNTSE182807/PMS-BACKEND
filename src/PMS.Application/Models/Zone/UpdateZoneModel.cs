namespace PMS.Application.Models.Zone;

public class UpdateZoneModel
{
    public string Name { get; set; }

    public string ZoneType { get; set; }

    public int? FloorNo { get; set; }

    public int Capacity { get; set; }

    public int? MaxReservationSlots { get; set; }

    public string LayoutJson { get; set; }

    public bool? IsActive { get; set; }
}
