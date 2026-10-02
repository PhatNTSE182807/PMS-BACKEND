namespace N_Tier.Application.Models.User;

public class CreateUserModel
{
    public string UserName { get; set; }

    public string Email { get; set; }

    public string Password { get; set; }

    public string PhoneNumber { get; set; }

    public string Role { get; set; } = "User";
}

public class CreateUserResponseModel : BaseResponseModel { }
