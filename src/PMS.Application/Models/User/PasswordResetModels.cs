namespace PMS.Application.Models.User;

/// <summary>Asks for a password reset link to be emailed.</summary>
public class ForgotPasswordModel
{
    public string Email { get; set; }
}

/// <summary>Sets a new password with the token from the reset link.</summary>
public class ResetPasswordModel
{
    public string Email { get; set; }

    /// <summary>The token exactly as it appears in the link (Base64Url).</summary>
    public string Token { get; set; }

    public string NewPassword { get; set; }

    public string ConfirmPassword { get; set; }
}
