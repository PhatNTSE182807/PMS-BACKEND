using FluentValidation;
using PMS.Application.Models.Profile;
using PMS.Application.Models.Validators.User;

namespace PMS.Application.Models.Validators.Profile;

public class ChangePasswordModelValidator : AbstractValidator<ChangePasswordModel>
{
    public ChangePasswordModelValidator()
    {
        RuleFor(x => x.CurrentPassword)
            .NotEmpty()
            .WithMessage("Current password is required.");

        RuleFor(x => x.NewPassword)
            .NotEmpty()
            .WithMessage("New password is required.")
            .MinimumLength(UserValidatorConfiguration.MinimumPasswordLength)
            .WithMessage($"New password must be at least {UserValidatorConfiguration.MinimumPasswordLength} characters.")
            .MaximumLength(UserValidatorConfiguration.MaximumPasswordLength)
            .WithMessage($"New password must not exceed {UserValidatorConfiguration.MaximumPasswordLength} characters.")
            .Matches(UserValidatorConfiguration.PasswordRegex)
            .WithMessage(UserValidatorConfiguration.PasswordRegexMessage)
            .NotEqual(x => x.CurrentPassword)
            .WithMessage("New password must be different from the current password.");

        RuleFor(x => x.ConfirmPassword)
            .NotEmpty()
            .WithMessage("Password confirmation is required.")
            .Equal(x => x.NewPassword)
            .WithMessage("Passwords do not match.");
    }
}
