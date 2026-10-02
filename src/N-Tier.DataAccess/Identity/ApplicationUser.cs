using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;

namespace N_Tier.DataAccess.Identity;

public class ApplicationUser : IdentityUser
{
    public string Role { get; set; } = "User";

    [NotMapped]
    public string UserId
    {
        get => Id;
        set => Id = value;
    }
}
