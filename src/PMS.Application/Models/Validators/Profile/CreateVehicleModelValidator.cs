using FluentValidation;
using PMS.Application.Helpers;
using PMS.Application.Models.Profile;

namespace PMS.Application.Models.Validators.Profile;

public class CreateVehicleModelValidator : AbstractValidator<CreateVehicleModel>
{
    internal const string InvalidPlateMessage =
        "License plate is not a valid Vietnamese plate (e.g. 51A-123.45, 30E-1234, 29-B1 123.45).";

    public CreateVehicleModelValidator()
    {
        RuleFor(x => x.LicensePlate)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .WithMessage("License plate is required.")
            .MaximumLength(20)
            .WithMessage("License plate must not exceed 20 characters.")
            .Must(LicensePlateHelper.IsValidVietnamesePlate)
            .WithMessage(InvalidPlateMessage);

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
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .WithMessage("License plate is required.")
            .MaximumLength(20)
            .WithMessage("License plate must not exceed 20 characters.")
            .Must(LicensePlateHelper.IsValidVietnamesePlate)
            .WithMessage(CreateVehicleModelValidator.InvalidPlateMessage);

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
