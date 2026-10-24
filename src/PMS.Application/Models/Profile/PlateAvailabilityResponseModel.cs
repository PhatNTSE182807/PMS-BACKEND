namespace PMS.Application.Models.Profile;

public class PlateAvailabilityResponseModel
{
    public string LicensePlate { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public string? Reason { get; set; }
}
