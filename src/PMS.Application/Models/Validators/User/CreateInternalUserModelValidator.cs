using System;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using PMS.Application.Models.User;
using PMS.DataAccess.Identity;

namespace PMS.Application.Models.Validators.User;

public class CreateInternalUserModelValidator : AbstractValidator<CreateInternalUserModel>
{
    private readonly UserManager<ApplicationUser> _userManager;

    public CreateInternalUserModelValidator(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;

        RuleFor(u => u.UserName)
            .NotEmpty()
            .WithMessage("Username is required")
            .MaximumLength(UserValidatorConfiguration.MaximumUsernameLength)
            .WithMessage($"Username should have maximum {UserValidatorConfiguration.MaximumUsernameLength} characters")
            .Must(UsernameIsUnique)
            .WithMessage("Username is not available");

        RuleFor(u => u.FullName)
            .MaximumLength(200)
            .WithMessage("Full name should have maximum 200 characters");

        // The Admin sets the password: there is no activation step for internal accounts
        RuleFor(u => u.Password)
            .NotEmpty()
            .WithMessage("Password is required")
            .MinimumLength(UserValidatorConfiguration.MinimumPasswordLength)
            .WithMessage($"Password should have minimum {UserValidatorConfiguration.MinimumPasswordLength} characters")
            .MaximumLength(UserValidatorConfiguration.MaximumPasswordLength)
            .WithMessage($"Password should have maximum {UserValidatorConfiguration.MaximumPasswordLength} characters")
            .Matches(UserValidatorConfiguration.PasswordRegex)
            .WithMessage(UserValidatorConfiguration.PasswordRegexMessage);

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

        RuleFor(u => u.Role)
            .NotEmpty()
            .WithMessage("Role is required")
            .Must(BeAllowedInternalRole)
            .WithMessage("Role must be either 'Manager' or 'Staff'");
    }

    private static bool BeAllowedInternalRole(string role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return false;

        return string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(role, "Staff", StringComparison.OrdinalIgnoreCase);
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
