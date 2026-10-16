namespace PMS.Application.Models.User;

/// <summary>POST /api/Users/login-google: the ID token (credential) from Google Identity Services.</summary>
public class GoogleLoginModel
{
    public string IdToken { get; set; }
}
