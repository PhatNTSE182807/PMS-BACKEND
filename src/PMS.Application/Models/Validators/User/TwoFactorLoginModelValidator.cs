using FluentValidation;
using PMS.Application.Models.User;

namespace PMS.Application.Models.Validators.User;

public class TwoFactorLoginModelValidator : AbstractValidator<TwoFactorLoginModel>
{
    public TwoFactorLoginModelValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("Verification code is required.")
            .Matches(@"^[0-9]{6}$").WithMessage("Verification code must be exactly 6 digits.");

        RuleFor(x => x)
            .Must(x => !string.IsNullOrWhiteSpace(x.TwoFactorToken) || !string.IsNullOrWhiteSpace(x.Email))
            .WithMessage("Either verification challenge token or email must be provided.");
    }
}

public class ResendTwoFactorModelValidator : AbstractValidator<ResendTwoFactorModel>
{
    public ResendTwoFactorModelValidator()
    {
        RuleFor(x => x)
            .Must(x => !string.IsNullOrWhiteSpace(x.TwoFactorToken) || !string.IsNullOrWhiteSpace(x.Email))
            .WithMessage("Either verification challenge token or email must be provided.");
    }
}
