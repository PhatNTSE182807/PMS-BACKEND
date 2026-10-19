namespace PMS.Application.Models.Profile;

public class CreateVehicleModel
{
    public string LicensePlate { get; set; }

    public int VehicleTypeId { get; set; }

    public string Brand { get; set; }

    public string Model { get; set; }

    public string Color { get; set; }

    /// <summary>Citizen ID (CCCD) of the registered owner on the vehicle papers; defaults to the account's own.</summary>
    public string CitizenId { get; set; }

    public bool IsDefault { get; set; }
}
