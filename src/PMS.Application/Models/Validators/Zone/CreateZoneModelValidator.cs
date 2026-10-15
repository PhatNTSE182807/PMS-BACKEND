using FluentValidation;
using PMS.Application.Models.Zone;

namespace PMS.Application.Models.Validators.Zone;

public class CreateZoneModelValidator : AbstractValidator<CreateZoneModel>
{
    public CreateZoneModelValidator()
    {
        RuleFor(x => x.ParkingLotId)
            .GreaterThan(0).WithMessage("Valid Parking lot ID is required.");

        RuleFor(x => x.Code)
            .MaximumLength(30).WithMessage("Zone code must not exceed 30 characters.")
            .Matches(@"^[A-Za-z0-9\-_]+$").When(x => !string.IsNullOrWhiteSpace(x.Code))
            .WithMessage("Zone code must contain only letters, numbers, dashes, and underscores.");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Zone name is required.")
            .MaximumLength(200).WithMessage("Zone name must not exceed 200 characters.");

        RuleFor(x => x.Capacity)
            .GreaterThanOrEqualTo(0).WithMessage("Capacity must be greater than or equal to 0.");

        RuleFor(x => x.ZoneType)
            .MaximumLength(20).WithMessage("Zone type must not exceed 20 characters.");

        RuleFor(x => x.MaxReservationSlots)
            .GreaterThanOrEqualTo(0)
            .When(x => x.MaxReservationSlots.HasValue)
            .WithMessage("Max reservation slots must be greater than or equal to 0.")
            .Must((model, maxRes) => !maxRes.HasValue || maxRes.Value <= model.Capacity)
            .WithMessage("Max reservation slots cannot exceed zone capacity.");
    }
}
