using FluentValidation;
using Microsoft.AspNetCore.Identity;
using PMS.Application.Models.Account;
using PMS.Application.Models.Validators.User;
using PMS.DataAccess.Identity;

namespace PMS.Application.Models.Validators.Account;

/// <summary>BR-28 form rules. Uniqueness on update is checked by the service, which knows the account id.</summary>
public class UpdateEmployeeAccountModelValidator : AbstractValidator<UpdateEmployeeAccountModel>
{
    public UpdateEmployeeAccountModelValidator()
    {
        RuleFor(u => u.FullName)
            .NotEmpty()
            .WithMessage("Full name is required")
            .Length(2, 100)
            .WithMessage("Full name should have between 2 and 100 characters");

        RuleFor(u => u.Email)
            .NotEmpty()
            .WithMessage("Email address is required")
            .Matches(UserValidatorConfiguration.EmailRegex)
            .WithMessage("Email address is not valid");

        RuleFor(u => u.PhoneNumber)
            .NotEmpty()
            .WithMessage("Phone number is required")
            .Matches(UserValidatorConfiguration.EmployeePhoneNumberRegex)
            .WithMessage("Phone number must have 10 digits and start with 0");
    }
}

public class CreateStaffAccountModelValidator : AbstractValidator<CreateStaffAccountModel>
{
    private readonly UserManager<ApplicationUser> _userManager;

    public CreateStaffAccountModelValidator(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;

        RuleFor(u => u.FullName)
            .NotEmpty()
            .WithMessage("Full name is required")
            .Length(2, 100)
            .WithMessage("Full name should have between 2 and 100 characters");

        RuleFor(u => u.Email)
            .NotEmpty()
            .WithMessage("Email address is required")
            .Matches(UserValidatorConfiguration.EmailRegex)
            .WithMessage("Email address is not valid")
            .Must(EmailAddressIsUnique)
            .WithMessage("Email address is already in use");

        RuleFor(u => u.PhoneNumber)
            .NotEmpty()
            .WithMessage("Phone number is required")
            .Matches(UserValidatorConfiguration.EmployeePhoneNumberRegex)
            .WithMessage("Phone number must have 10 digits and start with 0");

        RuleFor(u => u.Password)
            .NotEmpty()
            .WithMessage("Password is required")
            .MinimumLength(UserValidatorConfiguration.MinimumPasswordLength)
            .WithMessage($"Password should have minimum {UserValidatorConfiguration.MinimumPasswordLength} characters")
            .MaximumLength(UserValidatorConfiguration.MaximumPasswordLength)
            .WithMessage($"Password should have maximum {UserValidatorConfiguration.MaximumPasswordLength} characters")
            .Matches(UserValidatorConfiguration.PasswordRegex)
            .WithMessage(UserValidatorConfiguration.PasswordRegexMessage);

        When(u => u.ConfirmPassword != null, () =>
        {
            RuleFor(u => u.ConfirmPassword)
                .Equal(u => u.Password)
                .WithMessage("Passwords do not match");
        });

        When(u => !string.IsNullOrWhiteSpace(u.UserName), () =>
        {
            RuleFor(u => u.UserName)
                .Length(UserValidatorConfiguration.MinimumUsernameLength, UserValidatorConfiguration.MaximumUsernameLength)
                .WithMessage($"Username should have between {UserValidatorConfiguration.MinimumUsernameLength} and {UserValidatorConfiguration.MaximumUsernameLength} characters");
        });

        RuleFor(u => u.ParkingLotId)
            .GreaterThan(0)
            .WithMessage("Parking lot is required");
    }

    private bool EmailAddressIsUnique(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return true;

        var user = _userManager.FindByEmailAsync(email).GetAwaiter().GetResult();
        return user == null;
    }
}

public class AssignParkingLotsModelValidator : AbstractValidator<AssignParkingLotsModel>
{
    public AssignParkingLotsModelValidator()
    {
        RuleFor(m => m.ParkingLotIds)
            .NotNull()
            .WithMessage("Parking lot ids are required");

        RuleForEach(m => m.ParkingLotIds)
            .GreaterThan(0)
            .WithMessage("Parking lot id is not valid");
    }
}

public class AssignStaffParkingLotModelValidator : AbstractValidator<AssignStaffParkingLotModel>
{
    public AssignStaffParkingLotModelValidator()
    {
        RuleFor(m => m.ParkingLotId)
            .GreaterThan(0)
            .WithMessage("Parking lot is required");
    }
}
