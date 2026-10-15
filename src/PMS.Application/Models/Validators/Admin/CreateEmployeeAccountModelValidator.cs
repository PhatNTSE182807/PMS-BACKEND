using FluentValidation;
using Microsoft.AspNetCore.Identity;
using PMS.Application.Models.Admin;
using PMS.Application.Models.Validators.User;
using PMS.DataAccess.Identity;

namespace PMS.Application.Models.Validators.Admin;

public class CreateEmployeeAccountModelValidator : AbstractValidator<CreateEmployeeAccountModel>
{
    private readonly UserManager<ApplicationUser> _userManager;

    public CreateEmployeeAccountModelValidator(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;

        RuleFor(u => u.UserName)
            .NotEmpty()
            .WithMessage("Username is required")
            .MinimumLength(UserValidatorConfiguration.MinimumUsernameLength)
            .WithMessage($"Username should have minimum {UserValidatorConfiguration.MinimumUsernameLength} characters")
            .MaximumLength(UserValidatorConfiguration.MaximumUsernameLength)
            .WithMessage($"Username should have maximum {UserValidatorConfiguration.MaximumUsernameLength} characters")
            .Must(UsernameIsUnique)
            .WithMessage("Username is not available");

        RuleFor(u => u.FullName)
            .MaximumLength(200)
            .WithMessage("Full name should have maximum 200 characters");

        RuleFor(u => u.Password)
            .MinimumLength(UserValidatorConfiguration.MinimumPasswordLength)
            .WithMessage($"Password should have minimum {UserValidatorConfiguration.MinimumPasswordLength} characters")
            .MaximumLength(UserValidatorConfiguration.MaximumPasswordLength)
            .WithMessage($"Password should have maximum {UserValidatorConfiguration.MaximumPasswordLength} characters")
            .When(u => u.Password != null);

        RuleFor(u => u.Email)
            .NotEmpty()
            .WithMessage("Email address is required")
            .EmailAddress()
            .WithMessage("Email address is not valid")
            .Matches(UserValidatorConfiguration.EmailRegex)
            .WithMessage("Email address is not valid")
            .Must(EmailAddressIsUnique)
            .WithMessage("Email address is already in use");

        RuleFor(u => u.PhoneNumber)
            .NotEmpty()
            .WithMessage("Phone number is required")
            .Matches(UserValidatorConfiguration.VietnamPhoneNumberRegex)
            .WithMessage("Phone number is not a valid Vietnamese phone number");
    }

    private bool EmailAddressIsUnique(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return true;

        var user = _userManager.FindByEmailAsync(email).GetAwaiter().GetResult();
        return user == null;
    }

    private bool UsernameIsUnique(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return true;

        var user = _userManager.FindByNameAsync(username).GetAwaiter().GetResult();
        return user == null;
    }
}
