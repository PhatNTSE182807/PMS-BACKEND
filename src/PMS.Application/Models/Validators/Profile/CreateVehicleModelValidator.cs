using FluentValidation;
using PMS.Application.Models.Profile;
using PMS.Application.Models.Validators.User;

namespace PMS.Application.Models.Validators.Profile;

public class CreateVehicleModelValidator : AbstractValidator<CreateVehicleModel>
{
    public CreateVehicleModelValidator()
    {
        RuleFor(x => x.LicensePlate)
            .NotEmpty()
            .WithMessage("License plate is required.")
            .MaximumLength(20)
            .WithMessage("License plate must not exceed 20 characters.")
            .Matches(UserValidatorConfiguration.VietnamLicensePlateRegex)
            .WithMessage("License plate must follow a valid Vietnamese format (e.g. 29A-12345, 51A1-1234, 29-AB 12345).")
            .When(x => !string.IsNullOrWhiteSpace(x.LicensePlate));

        RuleFor(x => x.VehicleTypeId)
            .GreaterThan(0)
            .WithMessage("A valid vehicle type must be selected.");

        RuleFor(x => x.Brand)
            .MaximumLength(100)
            .WithMessage("Brand must not exceed 100 characters.");

        RuleFor(x => x.Model)
            .MaximumLength(100)
            .WithMessage("Model must not exceed 100 characters.");

        RuleFor(x => x.Color)
            .MaximumLength(50)
            .WithMessage("Color must not exceed 50 characters.");
    }
}

public class UpdateVehicleModelValidator : AbstractValidator<UpdateVehicleModel>
{
    public UpdateVehicleModelValidator()
    {
        RuleFor(x => x.LicensePlate)
            .NotEmpty()
            .WithMessage("License plate is required.")
            .MaximumLength(20)
            .WithMessage("License plate must not exceed 20 characters.")
            .Matches(UserValidatorConfiguration.VietnamLicensePlateRegex)
            .WithMessage("License plate must follow a valid Vietnamese format (e.g. 29A-12345, 51A1-1234, 29-AB 12345).")
            .When(x => !string.IsNullOrWhiteSpace(x.LicensePlate));

        RuleFor(x => x.VehicleTypeId)
            .GreaterThan(0)
            .WithMessage("A valid vehicle type must be selected.");

        RuleFor(x => x.Brand)
            .MaximumLength(100)
            .WithMessage("Brand must not exceed 100 characters.");

        RuleFor(x => x.Model)
            .MaximumLength(100)
            .WithMessage("Model must not exceed 100 characters.");

        RuleFor(x => x.Color)
            .MaximumLength(50)
            .WithMessage("Color must not exceed 50 characters.");
    }
}
