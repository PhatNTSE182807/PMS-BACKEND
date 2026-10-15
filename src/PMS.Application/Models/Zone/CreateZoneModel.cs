namespace PMS.Application.Models.Zone;

public class CreateZoneModel
{
    public int ParkingLotId { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }

    public string ZoneType { get; set; } = "Area";

    public string Type
    {
        get => ZoneType;
        set => ZoneType = value;
    }

    public int? FloorNo { get; set; }

    public int Capacity { get; set; }

    public int? MaxReservationSlots { get; set; }

    public string LayoutJson { get; set; }
}
