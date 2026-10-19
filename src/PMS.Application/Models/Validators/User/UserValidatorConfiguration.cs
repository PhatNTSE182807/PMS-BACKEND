namespace PMS.Application.Models.Validators.User;

public static class UserValidatorConfiguration
{
    public const int MinimumUsernameLength = 5;

    public const int MaximumUsernameLength = 20;

    public const int MinimumPasswordLength = 6;

    public const int MaximumPasswordLength = 128;

    public const string VietnamPhoneNumberRegex = @"^(\+84|84|0)[35789][0-9]{8}$";

    /// <summary>BR-28: Manager/Staff accounts need at least 8 characters with upper case, lower case and a digit.</summary>
    public const int MinimumEmployeePasswordLength = 8;

    public const string EmployeePasswordRegex = @"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d).+$";

    /// <summary>BR-28: a Vietnamese phone number of 10 digits starting with 0.</summary>
    public const string EmployeePhoneNumberRegex = @"^0[0-9]{9}$";

    /// <summary>Vietnamese Citizen ID (CCCD): exactly 12 digits.</summary>
    public const string CitizenIdRegex = @"^\d{12}$";

    public const string CitizenIdMessage = "Citizen ID must have exactly 12 digits";

    public const string EmailRegex = @"^[a-zA-Z0-9_%+-]+(?:\.[a-zA-Z0-9_%+-]+)*@(?:[a-zA-Z0-9-]+\.)+[a-zA-Z]{2,}$";
}
