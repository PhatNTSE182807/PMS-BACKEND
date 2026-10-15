namespace PMS.Application.Models.User;

public class TwoFactorLoginModel
{
    public string TwoFactorToken { get; set; }

    public string Code { get; set; }

    public string Email { get; set; }

    public bool RememberMe { get; set; }
}

public class ResendTwoFactorModel
{
    public string TwoFactorToken { get; set; }

    public string Email { get; set; }
}

public class ResendTwoFactorResponseModel
{
    public bool Succeeded { get; set; }

    public string TwoFactorToken { get; set; }

    public string Destination { get; set; }

    public string Message { get; set; }
}
