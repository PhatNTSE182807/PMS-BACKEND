namespace PMS.Application.Models.User;

public class CreateUserModel
{
    public string UserName { get; set; }

    public string FullName { get; set; }

    public string Email { get; set; }

    public string Password { get; set; }

    public string PhoneNumber { get; set; }
}

public class CreateUserResponseModel : BaseResponseModel
{
    public string EmployeeCode { get; set; }

    public string ActivationToken { get; set; }
}
