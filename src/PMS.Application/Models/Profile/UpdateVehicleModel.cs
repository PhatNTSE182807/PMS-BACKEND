namespace PMS.Application.Models.Profile;

public class UpdateVehicleModel
{
    public string LicensePlate { get; set; }

    public int VehicleTypeId { get; set; }

    public string Brand { get; set; }

    public string Model { get; set; }

    public string Color { get; set; }

    public bool IsDefault { get; set; }

    public bool IsActive { get; set; } = true;
}
