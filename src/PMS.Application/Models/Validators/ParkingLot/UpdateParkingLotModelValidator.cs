using FluentValidation;
using PMS.Application.Models.ParkingLot;

namespace PMS.Application.Models.Validators.ParkingLot;

public class UpdateParkingLotModelValidator : AbstractValidator<UpdateParkingLotModel>
{
    public UpdateParkingLotModelValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Parking lot name is required.")
            .MaximumLength(200).WithMessage("Parking lot name must not exceed 200 characters.");

        RuleFor(x => x.Address)
            .NotEmpty().WithMessage("Address is required.")
            .MaximumLength(500).WithMessage("Address must not exceed 500 characters.");

        RuleFor(x => x.OpenTime)
            .NotNull().When(x => !x.Is24Hours).WithMessage("Open time is required when parking lot is not 24 hours.");

        RuleFor(x => x.CloseTime)
            .NotNull().When(x => !x.Is24Hours).WithMessage("Close time is required when parking lot is not 24 hours.");

        RuleFor(x => x.Phone)
            .MaximumLength(30).WithMessage("Phone must not exceed 30 characters.");

        RuleFor(x => x.ImageUrl)
            .MaximumLength(500).WithMessage("Image URL must not exceed 500 characters.");

        RuleFor(x => x.Description)
            .MaximumLength(1000).WithMessage("Description must not exceed 1000 characters.");
    }
}
