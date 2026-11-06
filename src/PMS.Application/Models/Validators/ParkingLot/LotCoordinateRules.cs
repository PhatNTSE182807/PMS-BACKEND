using FluentValidation;

namespace PMS.Application.Models.Validators.ParkingLot;

/// <summary>Shared rules for a lot's map position: both values or neither, inside Vietnam.</summary>
public static class LotCoordinateRules
{
    // A loose box around Vietnam, islands included
    private const decimal MinLatitude = 8m, MaxLatitude = 24m, MinLongitude = 102m, MaxLongitude = 118m;

    // Each condition applies to its own check only: by default When() would cover the whole chain
    public static void AddCoordinateRules<T>(this AbstractValidator<T> validator, Func<T, decimal?> latitude, Func<T, decimal?> longitude)
    {
        validator.RuleFor(x => latitude(x))
            .NotNull().When(x => longitude(x) != null, ApplyConditionTo.CurrentValidator).WithMessage("Enter both latitude and longitude, or leave both empty.")
            .InclusiveBetween(MinLatitude, MaxLatitude).When(x => latitude(x) != null, ApplyConditionTo.CurrentValidator).WithMessage("Latitude must be between 8 and 24 (inside Vietnam).")
            .OverridePropertyName("Latitude");

        validator.RuleFor(x => longitude(x))
            .NotNull().When(x => latitude(x) != null, ApplyConditionTo.CurrentValidator).WithMessage("Enter both latitude and longitude, or leave both empty.")
            .InclusiveBetween(MinLongitude, MaxLongitude).When(x => longitude(x) != null, ApplyConditionTo.CurrentValidator).WithMessage("Longitude must be between 102 and 118 (inside Vietnam).")
            .OverridePropertyName("Longitude");
    }
}
