using System;
using System.Linq;
using FluentValidation;
using PMS.Application.Models.Pricing;

namespace PMS.Application.Models.Validators.Pricing;

public class CreatePricingRuleModelValidator : AbstractValidator<CreatePricingRuleModel>
{
    private static readonly string[] ValidChargeModels = ["Block", "Hourly", "PerEntry", "DayNight"];
    private static readonly string[] ValidStatuses = ["Draft", "Active", "Inactive", "Archived"];

    public CreatePricingRuleModelValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Rule name is required.")
            .MaximumLength(200).WithMessage("Rule name must not exceed 200 characters.");

        RuleFor(x => x.Code)
            .MaximumLength(40).WithMessage("Rule code must not exceed 40 characters.")
            .Matches(@"^[A-Za-z0-9\-_]+$").When(x => !string.IsNullOrWhiteSpace(x.Code))
            .WithMessage("Rule code must contain only letters, numbers, dashes, and underscores.");

        RuleFor(x => x.VehicleTypeId)
            .GreaterThan(0).WithMessage("Valid vehicle type ID is required.");

        RuleFor(x => x.ChargeModel)
            .NotEmpty().WithMessage("Charge model is required.")
            .Must(m => ValidChargeModels.Contains(m, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"Charge model must be one of: {string.Join(", ", ValidChargeModels)}.");

        RuleFor(x => x.EntryFee)
            .GreaterThanOrEqualTo(0).WithMessage("Entry fee must be greater than or equal to 0.");

        RuleFor(x => x.FirstBlockMinutes)
            .GreaterThan(0).When(x => x.FirstBlockMinutes.HasValue)
            .WithMessage("First block minutes must be greater than 0.");

        RuleFor(x => x.FirstBlockPrice)
            .GreaterThanOrEqualTo(0).When(x => x.FirstBlockPrice.HasValue)
            .WithMessage("First block price must be greater than or equal to 0.");

        RuleFor(x => x.BlockMinutes)
            .GreaterThan(0).When(x => x.BlockMinutes.HasValue)
            .WithMessage("Block minutes must be greater than 0.");

        RuleFor(x => x.BlockPrice)
            .GreaterThanOrEqualTo(0).When(x => x.BlockPrice.HasValue)
            .WithMessage("Block price must be greater than or equal to 0.");

        RuleFor(x => x.MaxDailyFee)
            .GreaterThanOrEqualTo(0).When(x => x.MaxDailyFee.HasValue)
            .WithMessage("Maximum daily fee must be greater than or equal to 0.");

        RuleFor(x => x.GraceMinutes)
            .GreaterThanOrEqualTo(0).WithMessage("Grace minutes must be greater than or equal to 0.");

        RuleFor(x => x.OvernightSurcharge)
            .GreaterThanOrEqualTo(0).WithMessage("Overnight surcharge must be greater than or equal to 0.");

        RuleFor(x => x.LostTicketFee)
            .GreaterThanOrEqualTo(0).WithMessage("Lost ticket fee must be greater than or equal to 0.");

        RuleFor(x => x.NoShowFee)
            .GreaterThanOrEqualTo(0).WithMessage("No show fee must be greater than or equal to 0.");

        RuleFor(x => x.Currency)
            .MaximumLength(3).WithMessage("Currency code must not exceed 3 characters.");

        RuleFor(x => x.Status)
            .Must(s => string.IsNullOrEmpty(s) || ValidStatuses.Contains(s, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"Status must be one of: {string.Join(", ", ValidStatuses)}.");

        RuleFor(x => x.EffectiveTo)
            .GreaterThan(x => x.EffectiveFrom!.Value)
            .When(x => x.EffectiveFrom.HasValue && x.EffectiveTo.HasValue)
            .WithMessage("Effective to date must be after effective from date.");
    }
}

public class UpdatePricingRuleModelValidator : AbstractValidator<UpdatePricingRuleModel>
{
    private static readonly string[] ValidChargeModels = ["Block", "Hourly", "PerEntry", "DayNight"];
    private static readonly string[] ValidStatuses = ["Draft", "Active", "Inactive", "Archived"];

    public UpdatePricingRuleModelValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Rule name is required.")
            .MaximumLength(200).WithMessage("Rule name must not exceed 200 characters.");

        RuleFor(x => x.VehicleTypeId)
            .GreaterThan(0).WithMessage("Valid vehicle type ID is required.");

        RuleFor(x => x.ChargeModel)
            .NotEmpty().WithMessage("Charge model is required.")
            .Must(m => ValidChargeModels.Contains(m, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"Charge model must be one of: {string.Join(", ", ValidChargeModels)}.");

        RuleFor(x => x.EntryFee)
            .GreaterThanOrEqualTo(0).WithMessage("Entry fee must be greater than or equal to 0.");

        RuleFor(x => x.FirstBlockMinutes)
            .GreaterThan(0).When(x => x.FirstBlockMinutes.HasValue)
            .WithMessage("First block minutes must be greater than 0.");

        RuleFor(x => x.FirstBlockPrice)
            .GreaterThanOrEqualTo(0).When(x => x.FirstBlockPrice.HasValue)
            .WithMessage("First block price must be greater than or equal to 0.");

        RuleFor(x => x.BlockMinutes)
            .GreaterThan(0).When(x => x.BlockMinutes.HasValue)
            .WithMessage("Block minutes must be greater than 0.");

        RuleFor(x => x.BlockPrice)
            .GreaterThanOrEqualTo(0).When(x => x.BlockPrice.HasValue)
            .WithMessage("Block price must be greater than or equal to 0.");

        RuleFor(x => x.MaxDailyFee)
            .GreaterThanOrEqualTo(0).When(x => x.MaxDailyFee.HasValue)
            .WithMessage("Maximum daily fee must be greater than or equal to 0.");

        RuleFor(x => x.GraceMinutes)
            .GreaterThanOrEqualTo(0).WithMessage("Grace minutes must be greater than or equal to 0.");

        RuleFor(x => x.OvernightSurcharge)
            .GreaterThanOrEqualTo(0).WithMessage("Overnight surcharge must be greater than or equal to 0.");

        RuleFor(x => x.LostTicketFee)
            .GreaterThanOrEqualTo(0).WithMessage("Lost ticket fee must be greater than or equal to 0.");

        RuleFor(x => x.NoShowFee)
            .GreaterThanOrEqualTo(0).WithMessage("No show fee must be greater than or equal to 0.");

        RuleFor(x => x.Status)
            .Must(s => string.IsNullOrEmpty(s) || ValidStatuses.Contains(s, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"Status must be one of: {string.Join(", ", ValidStatuses)}.");

        RuleFor(x => x.EffectiveTo)
            .GreaterThan(x => x.EffectiveFrom)
            .When(x => x.EffectiveTo.HasValue)
            .WithMessage("Effective to date must be after effective from date.");
    }
}

public class PricingSimulationRequestModelValidator : AbstractValidator<PricingSimulationRequestModel>
{
    public PricingSimulationRequestModelValidator()
    {
        RuleFor(x => x.ExitTime)
            .GreaterThanOrEqualTo(x => x.EntryTime)
            .WithMessage("Exit time must be greater than or equal to entry time.");

        RuleFor(x => x.DepositAmount)
            .GreaterThanOrEqualTo(0)
            .WithMessage("Deposit amount must be greater than or equal to 0.");

        RuleFor(x => x)
            .Must(x => x.RuleId.HasValue || x.VehicleTypeId.HasValue || !string.IsNullOrWhiteSpace(x.VehicleTypeCode))
            .WithMessage("At least one of RuleId, VehicleTypeId, or VehicleTypeCode must be provided for simulation.");
    }
}
