namespace PMS.Application.Models.Profile;

/// <summary>A stored profile photo, ready to be sent back as a file.</summary>
public class AvatarImageModel
{
    public byte[] Content { get; set; }

    public string ContentType { get; set; }
}
