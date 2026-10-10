namespace PMS.Application.Models.Validators.User;

public static class UserValidatorConfiguration
{
    public const int MinimumUsernameLength = 5;

    public const int MaximumUsernameLength = 20;

    public const int MinimumPasswordLength = 6;

    public const int MaximumPasswordLength = 128;

    public const string VietnamPhoneNumberRegex = @"^(\+84|84|0)[35789][0-9]{8}$";

    public const string EmailRegex = @"^[a-zA-Z0-9_%+-]+(?:\.[a-zA-Z0-9_%+-]+)*@(?:[a-zA-Z0-9-]+\.)+[a-zA-Z]{2,}$";

    /// <summary>
    /// Vietnamese license plate regex – matches common formats (after trim &amp; uppercase):
    ///   Motorcycle : 29-AB 12345, 29-A1 12345, 29-AB.12345
    ///   Car        : 29A-12345,   51A1-1234,   51A1-12345
    ///   5-digit    : 29A-123.45
    /// Province code: 11-99 (2-digit prefix per Vietnamese regulations)
    /// </summary>
    public const string VietnamLicensePlateRegex =
        @"^[0-9]{2}[A-Z]{1,2}[-\s]?[0-9]{3,5}(\.[0-9]{2})?$";
}
