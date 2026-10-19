namespace PMS.Application.Models.Profile;

public class UpdateUserProfileModel
{
    public string FullName { get; set; }

    public string Email { get; set; }

    public string PhoneNumber { get; set; }

    public string AvatarUrl { get; set; }

    /// <summary>Citizen ID (CCCD); accounts created with Google add it here. Left unchanged when empty.</summary>
    public string CitizenId { get; set; }
}
