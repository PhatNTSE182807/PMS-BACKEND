using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;

namespace PMS.DataAccess.Identity;

public class ApplicationUser : IdentityUser
{
    public string Role { get; set; } = "User";

    public bool IsActive { get; set; } = true;

    [NotMapped]
    public string UserId
    {
        get => Id;
        set => Id = value;
    }
}
