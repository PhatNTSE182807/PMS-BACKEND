namespace PMS.Application.Models.Profile;

public class CreateVehicleModel
{
    public string LicensePlate { get; set; }

    public int VehicleTypeId { get; set; }

    public string Brand { get; set; }

    public string Model { get; set; }

    public string Color { get; set; }

    public bool IsDefault { get; set; }
}
