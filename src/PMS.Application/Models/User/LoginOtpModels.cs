namespace PMS.Application.Models.User;

public class RequestLoginOtpModel
{
    public string Email { get; set; }
}

public class VerifyLoginOtpModel
{
    public string Email { get; set; }

    public string Code { get; set; }
}
