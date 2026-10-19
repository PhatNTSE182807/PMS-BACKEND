namespace PMS.Application.Models.User;

public class CreateUserModel
{
    public string UserName { get; set; }

    public string FullName { get; set; }

    public string Email { get; set; }

    public string Password { get; set; }

    public string PhoneNumber { get; set; }

    /// <summary>Citizen ID (CCCD): 12 digits, one account per Citizen ID.</summary>
    public string CitizenId { get; set; }
}

public class CreateUserResponseModel : BaseResponseModel
{
    public string EmployeeCode { get; set; }
}
