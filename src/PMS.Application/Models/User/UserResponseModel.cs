namespace PMS.Application.Models.User;

public class UserResponseModel
{
    public string Id { get; set; }

    public string UserName { get; set; }

    public string FullName { get; set; }

    public string Email { get; set; }

    public string PhoneNumber { get; set; }

    public string Role { get; set; }

    public string EmployeeCode { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>When the employee record was created; null for customer accounts.</summary>
    public DateTime? CreatedAt { get; set; }

    /// <summary>Manager/Staff accounts that have not set their password from the activation email yet.</summary>
    public bool IsPendingActivation { get; set; }
}
