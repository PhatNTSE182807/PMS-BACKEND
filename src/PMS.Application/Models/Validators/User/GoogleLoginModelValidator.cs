using FluentValidation;
using PMS.Application.Models.User;

namespace PMS.Application.Models.Validators.User;

public class GoogleLoginModelValidator : AbstractValidator<GoogleLoginModel>
{
    public GoogleLoginModelValidator()
    {
        RuleFor(m => m.IdToken)
            .NotEmpty()
            .WithMessage("Google credential is required");
    }
}
