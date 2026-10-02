namespace N_Tier.Application.Models.Validators.User;

public static class UserValidatorConfiguration
{
    public const int MinimumUsernameLength = 5;

    public const int MaximumUsernameLength = 20;

    public const int MinimumPasswordLength = 6;

    public const int MaximumPasswordLength = 128;

    public const string VietnamPhoneNumberRegex = @"^(\+84|84|0)[35789][0-9]{8}$";

    public const string EmailRegex = @"^[a-zA-Z0-9_%+-]+(?:\.[a-zA-Z0-9_%+-]+)*@(?:[a-zA-Z0-9-]+\.)+[a-zA-Z]{2,}$";
}
