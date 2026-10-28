using FluentValidation;
using PMS.Application.Models.User;

namespace PMS.Application.Models.Validators.User;

public class ForgotPasswordModelValidator : AbstractValidator<ForgotPasswordModel>
{
    public ForgotPasswordModelValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .WithMessage("Email address is required")
            .EmailAddress()
            .WithMessage("Email address is not valid");
    }
}

public class ResetPasswordModelValidator : AbstractValidator<ResetPasswordModel>
{
    public ResetPasswordModelValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .WithMessage("Email address is required")
            .EmailAddress()
            .WithMessage("Email address is not valid");

        RuleFor(x => x.Token)
            .NotEmpty()
            .WithMessage("The reset link is incomplete. Please open the link from the email again.");

        // Same rule as registration and change password (team rule, AGENTS.md section 10)
        RuleFor(x => x.NewPassword)
            .NotEmpty()
            .WithMessage("New password is required.")
            .MinimumLength(UserValidatorConfiguration.MinimumPasswordLength)
            .WithMessage($"New password must be at least {UserValidatorConfiguration.MinimumPasswordLength} characters.")
            .MaximumLength(UserValidatorConfiguration.MaximumPasswordLength)
            .WithMessage($"New password must not exceed {UserValidatorConfiguration.MaximumPasswordLength} characters.")
            .Matches(UserValidatorConfiguration.PasswordRegex)
            .WithMessage(UserValidatorConfiguration.PasswordRegexMessage);

        RuleFor(x => x.ConfirmPassword)
            .NotEmpty()
            .WithMessage("Password confirmation is required.")
            .Equal(x => x.NewPassword)
            .WithMessage("Passwords do not match.");
    }
}
