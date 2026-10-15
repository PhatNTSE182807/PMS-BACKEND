using System;
using FluentValidation;
using PMS.Application.Models.Slot;

namespace PMS.Application.Models.Validators.Slot;

public class CreateSlotModelValidator : AbstractValidator<CreateSlotModel>
{
    private static readonly string[] AllowedStatuses = ["Available", "Occupied", "Reserved", "Maintenance"];

    public CreateSlotModelValidator()
    {
        RuleFor(x => x.ZoneId)
            .GreaterThan(0).WithMessage("Valid Zone ID is required.");

        RuleFor(x => x.VehicleTypeId)
            .GreaterThan(0).WithMessage("Valid Vehicle Type ID is required.");

        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("Slot code is required.")
            .MaximumLength(30).WithMessage("Slot code must not exceed 30 characters.")
            .Matches(@"^[A-Za-z0-9\-_]+$").WithMessage("Slot code must contain only letters, numbers, dashes, and underscores.");

        RuleFor(x => x.Status)
            .Must(s => string.IsNullOrEmpty(s) || Array.Exists(AllowedStatuses, a => string.Equals(a, s, StringComparison.OrdinalIgnoreCase)))
            .WithMessage("Invalid status. Allowed values: Available, Occupied, Reserved, Maintenance.");

        RuleFor(x => x.DedicatedNote)
            .MaximumLength(200).WithMessage("Dedicated note must not exceed 200 characters.");
    }
}
