namespace PMS.Application.Models.Validators.User;

public static class UserValidatorConfiguration
{


    public const int MaximumUsernameLength = 20;

    /// <summary>Every account (customer, Manager, Staff, Admin): at least 6 characters.</summary>
    public const int MinimumPasswordLength = 6;

    public const int MaximumPasswordLength = 128;

    /// <summary>At least one upper case letter and one special (non-alphanumeric) character.</summary>
    public const string PasswordRegex = @"^(?=.*[A-Z])(?=.*[^A-Za-z0-9]).+$";

    public const string PasswordRegexMessage = "Password must contain at least one upper case letter and one special character";

    public const string VietnamPhoneNumberRegex = @"^(\+84|84|0)[35789][0-9]{8}$";

    /// <summary>BR-28: a Vietnamese phone number of 10 digits starting with 0.</summary>
    public const string EmployeePhoneNumberRegex = @"^0[0-9]{9}$";

    /// <summary>Vietnamese Citizen ID (CCCD): exactly 12 digits.</summary>
    public const string CitizenIdRegex = @"^\d{12}$";

    public const string CitizenIdMessage = "Citizen ID must have exactly 12 digits";

    public const string EmailRegex = @"^[a-zA-Z0-9_%+-]+(?:\.[a-zA-Z0-9_%+-]+)*@(?:[a-zA-Z0-9-]+\.)+[a-zA-Z]{2,}$";
}
