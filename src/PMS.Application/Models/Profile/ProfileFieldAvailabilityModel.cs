namespace PMS.Application.Models.Profile;

public class ProfileFieldAvailabilityModel
{
    public bool IsPhoneAvailable { get; set; } = true;
    public string? PhoneReason { get; set; }
    public bool IsCitizenIdAvailable { get; set; } = true;
    public string? CitizenIdReason { get; set; }
}
