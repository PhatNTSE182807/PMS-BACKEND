using FluentValidation;
using PMS.Application.Models.Slot;

namespace PMS.Application.Models.Validators.Slot;

public class UpdateSlotModelValidator : AbstractValidator<UpdateSlotModel>
{
    public UpdateSlotModelValidator()
    {
        RuleFor(x => x.VehicleTypeId)
            .GreaterThan(0).When(x => x.VehicleTypeId.HasValue).WithMessage("Valid Vehicle Type ID is required.");

        RuleFor(x => x.Code)
            .MaximumLength(30).When(x => !string.IsNullOrEmpty(x.Code)).WithMessage("Slot code must not exceed 30 characters.")
            .Matches(@"^[A-Za-z0-9\-_]+$").When(x => !string.IsNullOrEmpty(x.Code)).WithMessage("Slot code must contain only letters, numbers, dashes, and underscores.");

        RuleFor(x => x.DedicatedNote)
            .MaximumLength(200).WithMessage("Dedicated note must not exceed 200 characters.");
    }
}
