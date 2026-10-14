using FluentValidation;
using PMS.Application.Models.User;

namespace PMS.Application.Models.Validators.User;

public class LoginUserModelValidator : AbstractValidator<LoginUserModel>
{
    public LoginUserModelValidator()
    {
        RuleFor(u => u.Email)
            .NotEmpty()
            .WithMessage("Email address is required")
            .EmailAddress()
            .WithMessage("Email address is not valid");

        RuleFor(u => u.Password)
            .NotEmpty()
            .WithMessage("Password is required");
    }
}
