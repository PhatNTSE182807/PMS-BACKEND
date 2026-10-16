using FluentValidation;
using PMS.Application.Models.User;

namespace PMS.Application.Models.Validators.User;

public class LoginUserModelValidator : AbstractValidator<LoginUserModel>
{
    public LoginUserModelValidator()
    {
        // Customers sign in with an email, internal accounts (Manager/Staff) with a username
        RuleFor(u => u.Email)
            .NotEmpty()
            .When(u => string.IsNullOrWhiteSpace(u.Username))
            .WithMessage("Email address is required");

        RuleFor(u => u.Email)
            .EmailAddress()
            .When(u => !string.IsNullOrWhiteSpace(u.Email))
            .WithMessage("Email address is not valid");

        RuleFor(u => u.Password)
            .NotEmpty()
            .WithMessage("Password is required");
    }
}
