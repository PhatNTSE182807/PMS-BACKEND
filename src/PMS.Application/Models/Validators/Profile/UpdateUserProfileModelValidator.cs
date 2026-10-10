using FluentValidation;
using PMS.Application.Models.Profile;
using PMS.Application.Models.Validators.User;

namespace PMS.Application.Models.Validators.Profile;

public class UpdateUserProfileModelValidator : AbstractValidator<UpdateUserProfileModel>
{
    public UpdateUserProfileModelValidator()
    {
        RuleFor(x => x.FullName)
            .MaximumLength(200)
            .WithMessage("Full name must not exceed 200 characters.");

        When(x => !string.IsNullOrWhiteSpace(x.Email), () =>
        {
            RuleFor(x => x.Email)
                .EmailAddress()
                .WithMessage("Invalid email address format.")
                .Matches(UserValidatorConfiguration.EmailRegex)
                .WithMessage("Invalid email address format.")
                .MaximumLength(256)
                .WithMessage("Email must not exceed 256 characters.");
        });

        When(x => !string.IsNullOrWhiteSpace(x.PhoneNumber), () =>
        {
            RuleFor(x => x.PhoneNumber)
                .Matches(UserValidatorConfiguration.VietnamPhoneNumberRegex)
                .WithMessage("Phone number must be a valid format.");
        });

        When(x => !string.IsNullOrWhiteSpace(x.AvatarUrl), () =>
        {
            RuleFor(x => x.AvatarUrl)
                .MaximumLength(500)
                .WithMessage("Avatar URL must not exceed 500 characters.");
        });
    }
}
