namespace PMS.Application.Models.User;

public class LoginUserModel
{
    public string Email { get; set; }

    public string Password { get; set; }
}

public class LoginResponseModel
{
    public string UserId { get; set; }

    public string Username { get; set; }

    public string Email { get; set; }

    public string PhoneNumber { get; set; }

    public string Role { get; set; }

    public string Token { get; set; }
}
