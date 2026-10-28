using System;
using FluentValidation.TestHelper;
using PMS.Application.Models.Pricing;
using PMS.Application.Models.Validators.Pricing;
using Xunit;

namespace PMS.Application.UnitTests.Validators;

public class PricingRuleModelValidatorTests
{
    private readonly CreatePricingRuleModelValidator _create = new();
    private readonly UpdatePricingRuleModelValidator _update = new();
    private readonly PricingSimulationRequestModelValidator _simulation = new();

    private static CreatePricingRuleModel ValidCreate() => new()
    {
        Code = "CAR_4-STD",
        Name = "Car standard",
        VehicleTypeId = 1,
        ChargeModel = "Block",
        FirstBlockMinutes = 60,
        FirstBlockPrice = 20000,
        BlockMinutes = 60,
        BlockPrice = 10000,
        MaxDailyFee = 150000,
        EffectiveFrom = new DateTime(2026, 1, 1),
        EffectiveTo = new DateTime(2026, 12, 31),
        Status = "Active"
    };

    private static UpdatePricingRuleModel ValidUpdate() => new()
    {
        Name = "Car standard",
        VehicleTypeId = 1,
        ChargeModel = "dayNight",
        FirstBlockMinutes = 60,
        FirstBlockPrice = 20000,
        BlockMinutes = 60,
        BlockPrice = 10000,
        MaxDailyFee = 150000,
        EffectiveFrom = new DateTime(2026, 1, 1),
        EffectiveTo = new DateTime(2026, 12, 31),
        Status = "draft"
    };

    [Fact]
    public void Create_Should_Accept_A_Valid_Rule()
    {
        _create.TestValidate(ValidCreate()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Create_Should_Accept_Empty_Code_And_Status()
    {
        var model = ValidCreate();
        model.Code = null;
        model.Status = null;
        model.EffectiveFrom = null;

        _create.TestValidate(model).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Create_Should_Reject_Every_Invalid_Field()
    {
        var model = new CreatePricingRuleModel
        {
            Code = "bad code!",
            Name = "",
            VehicleTypeId = 0,
            ChargeModel = "Monthly",
            EntryFee = -1,
            FirstBlockMinutes = 0,
            FirstBlockPrice = -1,
            BlockMinutes = 0,
            BlockPrice = -1,
            MaxDailyFee = -1,
            GraceMinutes = -1,
            OvernightSurcharge = -1,
            LostTicketFee = -1,
            NoShowFee = -1,
            Currency = "VNDX",
            Status = "Deleted",
            EffectiveFrom = new DateTime(2026, 2, 1),
            EffectiveTo = new DateTime(2026, 1, 1)
        };

        var result = _create.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Code);
        result.ShouldHaveValidationErrorFor(x => x.Name);
        result.ShouldHaveValidationErrorFor(x => x.VehicleTypeId);
        result.ShouldHaveValidationErrorFor(x => x.ChargeModel);
        result.ShouldHaveValidationErrorFor(x => x.EntryFee);
        result.ShouldHaveValidationErrorFor(x => x.FirstBlockMinutes);
        result.ShouldHaveValidationErrorFor(x => x.FirstBlockPrice);
        result.ShouldHaveValidationErrorFor(x => x.BlockMinutes);
        result.ShouldHaveValidationErrorFor(x => x.BlockPrice);
        result.ShouldHaveValidationErrorFor(x => x.MaxDailyFee);
        result.ShouldHaveValidationErrorFor(x => x.GraceMinutes);
        result.ShouldHaveValidationErrorFor(x => x.OvernightSurcharge);
        result.ShouldHaveValidationErrorFor(x => x.LostTicketFee);
        result.ShouldHaveValidationErrorFor(x => x.NoShowFee);
        result.ShouldHaveValidationErrorFor(x => x.Currency);
        result.ShouldHaveValidationErrorFor(x => x.Status);
        result.ShouldHaveValidationErrorFor(x => x.EffectiveTo);
    }

    [Fact]
    public void Create_Should_Reject_A_Name_Over_200_Characters()
    {
        var model = ValidCreate();
        model.Name = new string('a', 201);

        _create.TestValidate(model).ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Update_Should_Accept_A_Valid_Rule()
    {
        _update.TestValidate(ValidUpdate()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Update_Should_Accept_An_Open_Ended_Rule_Without_Status()
    {
        var model = ValidUpdate();
        model.EffectiveTo = null;
        model.Status = null;

        _update.TestValidate(model).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Update_Should_Reject_Every_Invalid_Field()
    {
        var model = new UpdatePricingRuleModel
        {
            Name = null,
            VehicleTypeId = -1,
            ChargeModel = "",
            EntryFee = -1,
            FirstBlockMinutes = -5,
            FirstBlockPrice = -1,
            BlockMinutes = -5,
            BlockPrice = -1,
            MaxDailyFee = -1,
            GraceMinutes = -1,
            OvernightSurcharge = -1,
            LostTicketFee = -1,
            NoShowFee = -1,
            Status = "Unknown",
            EffectiveFrom = new DateTime(2026, 2, 1),
            EffectiveTo = new DateTime(2026, 2, 1)
        };

        var result = _update.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Name);
        result.ShouldHaveValidationErrorFor(x => x.VehicleTypeId);
        result.ShouldHaveValidationErrorFor(x => x.ChargeModel);
        result.ShouldHaveValidationErrorFor(x => x.EntryFee);
        result.ShouldHaveValidationErrorFor(x => x.FirstBlockMinutes);
        result.ShouldHaveValidationErrorFor(x => x.FirstBlockPrice);
        result.ShouldHaveValidationErrorFor(x => x.BlockMinutes);
        result.ShouldHaveValidationErrorFor(x => x.BlockPrice);
        result.ShouldHaveValidationErrorFor(x => x.MaxDailyFee);
        result.ShouldHaveValidationErrorFor(x => x.GraceMinutes);
        result.ShouldHaveValidationErrorFor(x => x.OvernightSurcharge);
        result.ShouldHaveValidationErrorFor(x => x.LostTicketFee);
        result.ShouldHaveValidationErrorFor(x => x.NoShowFee);
        result.ShouldHaveValidationErrorFor(x => x.Status);
        result.ShouldHaveValidationErrorFor(x => x.EffectiveTo);
    }

    [Theory]
    [InlineData(1, null, null)]
    [InlineData(null, 2, null)]
    [InlineData(null, null, "CAR_4")]
    public void Simulation_Should_Accept_Any_Rule_Selector(int? ruleId, int? vehicleTypeId, string vehicleTypeCode)
    {
        var model = new PricingSimulationRequestModel
        {
            RuleId = ruleId,
            VehicleTypeId = vehicleTypeId,
            VehicleTypeCode = vehicleTypeCode,
            EntryTime = new DateTime(2026, 1, 1, 8, 0, 0),
            ExitTime = new DateTime(2026, 1, 1, 10, 0, 0)
        };

        _simulation.TestValidate(model).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Simulation_Should_Reject_Bad_Times_Deposit_And_Missing_Selector()
    {
        var model = new PricingSimulationRequestModel
        {
            VehicleTypeCode = " ",
            EntryTime = new DateTime(2026, 1, 1, 10, 0, 0),
            ExitTime = new DateTime(2026, 1, 1, 8, 0, 0),
            DepositAmount = -1
        };

        var result = _simulation.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.ExitTime);
        result.ShouldHaveValidationErrorFor(x => x.DepositAmount);
        result.Errors.Should_Contain("At least one of RuleId");
    }
}

internal static class ValidationErrorAssertions
{
    /// <summary>Asserts that one of the errors contains the given text.</summary>
    public static void Should_Contain(this System.Collections.Generic.List<FluentValidation.Results.ValidationFailure> errors, string text)
    {
        Assert.Contains(errors, e => e.ErrorMessage.Contains(text, StringComparison.Ordinal));
    }
}
