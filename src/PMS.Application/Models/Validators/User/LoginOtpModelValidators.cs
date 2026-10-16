using FluentValidation;
using PMS.Application.Models.User;

namespace PMS.Application.Models.Validators.User;

public class RequestLoginOtpModelValidator : AbstractValidator<RequestLoginOtpModel>
{
    public RequestLoginOtpModelValidator()
    {
        RuleFor(u => u.Email)
            .NotEmpty()
            .WithMessage("Email address is required")
            .EmailAddress()
            .WithMessage("Email address is not valid");
    }
}

public class VerifyLoginOtpModelValidator : AbstractValidator<VerifyLoginOtpModel>
{
    public VerifyLoginOtpModelValidator()
    {
        RuleFor(u => u.Email)
            .NotEmpty()
            .WithMessage("Email address is required")
            .EmailAddress()
            .WithMessage("Email address is not valid");

        RuleFor(u => u.Code)
            .NotEmpty()
            .WithMessage("Login code is required")
            .Matches(@"^\d{6}$")
            .WithMessage("Login code must be 6 digits");
    }
}
