using FluentValidation;
using PMS.Application.Models.User;

namespace PMS.Application.Models.Validators.User;

public class ActivateAccountModelValidator : AbstractValidator<ActivateAccountModel>
{
    public ActivateAccountModelValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty()
            .WithMessage("User ID is required.");

        RuleFor(x => x.Token)
            .NotEmpty()
            .WithMessage("Activation token is required.");

        RuleFor(x => x.NewPassword)
            .NotEmpty()
            .WithMessage("New password is required.")
            .MinimumLength(UserValidatorConfiguration.MinimumPasswordLength)
            .WithMessage($"New password must be at least {UserValidatorConfiguration.MinimumPasswordLength} characters.")
            .MaximumLength(UserValidatorConfiguration.MaximumPasswordLength)
            .WithMessage($"New password must not exceed {UserValidatorConfiguration.MaximumPasswordLength} characters.");

        RuleFor(x => x.ConfirmPassword)
            .NotEmpty()
            .WithMessage("Password confirmation is required.")
            .Equal(x => x.NewPassword)
            .WithMessage("Passwords do not match.");
    }
}
