using System;
using FluentValidation.TestHelper;
using PMS.Application.Models.ParkingLot;
using PMS.Application.Models.Slot;
using PMS.Application.Models.Validators.ParkingLot;
using PMS.Application.Models.Validators.Slot;
using PMS.Application.Models.Validators.Zone;
using PMS.Application.Models.Zone;
using Xunit;

namespace PMS.Application.UnitTests.Validators;

public class LotZoneSlotModelValidatorTests
{
    // ---- Parking lots ----

    [Fact]
    public void CreateLot_Should_Accept_A_24_Hour_Lot_From_A_Known_Template()
    {
        var model = new CreateParkingLotModel
        {
            Code = "LOT-01",
            Name = "Central",
            Address = "1 Main Street",
            LayoutTemplate = "L3S20",
            Is24Hours = true
        };

        new CreateParkingLotModelValidator().TestValidate(model).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void CreateLot_Should_Reject_Missing_Fields_Unknown_Template_And_Missing_Hours()
    {
        var model = new CreateParkingLotModel
        {
            Code = "bad code",
            Name = "",
            Address = "",
            LayoutTemplate = "NOPE",
            Is24Hours = false,
            Phone = new string('1', 31),
            ImageUrl = new string('a', 501),
            Description = new string('a', 1001)
        };

        var result = new CreateParkingLotModelValidator().TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Code);
        result.ShouldHaveValidationErrorFor(x => x.Name);
        result.ShouldHaveValidationErrorFor(x => x.Address);
        result.ShouldHaveValidationErrorFor(x => x.LayoutTemplate);
        result.ShouldHaveValidationErrorFor(x => x.OpenTime);
        result.ShouldHaveValidationErrorFor(x => x.CloseTime);
        result.ShouldHaveValidationErrorFor(x => x.Phone);
        result.ShouldHaveValidationErrorFor(x => x.ImageUrl);
        result.ShouldHaveValidationErrorFor(x => x.Description);
    }

    [Fact]
    public void UpdateLot_Should_Accept_A_Lot_With_Opening_Hours()
    {
        var model = new UpdateParkingLotModel
        {
            Name = "Central",
            Address = "1 Main Street",
            Is24Hours = false,
            OpenTime = new TimeOnly(6, 0),
            CloseTime = new TimeOnly(22, 0)
        };

        new UpdateParkingLotModelValidator().TestValidate(model).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void UpdateLot_Should_Reject_Missing_Fields()
    {
        var model = new UpdateParkingLotModel
        {
            Name = new string('a', 201),
            Address = null,
            Is24Hours = false,
            Phone = new string('1', 31),
            ImageUrl = new string('a', 501),
            Description = new string('a', 1001)
        };

        var result = new UpdateParkingLotModelValidator().TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Name);
        result.ShouldHaveValidationErrorFor(x => x.Address);
        result.ShouldHaveValidationErrorFor(x => x.OpenTime);
        result.ShouldHaveValidationErrorFor(x => x.CloseTime);
        result.ShouldHaveValidationErrorFor(x => x.Phone);
        result.ShouldHaveValidationErrorFor(x => x.ImageUrl);
        result.ShouldHaveValidationErrorFor(x => x.Description);
    }

    // ---- Zones ----

    [Fact]
    public void CreateZone_Should_Accept_A_Valid_Zone()
    {
        var model = new CreateZoneModel
        {
            ParkingLotId = 1,
            Code = "Z-1",
            Name = "Level 1",
            Type = "Floor",
            Capacity = 20,
            MaxReservationSlots = 5
        };

        new CreateZoneModelValidator().TestValidate(model).ShouldNotHaveAnyValidationErrors();
        Assert.Equal("Floor", model.ZoneType);
        Assert.Equal("Floor", model.Type);
    }

    [Fact]
    public void CreateZone_Should_Reject_Invalid_Values()
    {
        var model = new CreateZoneModel
        {
            ParkingLotId = 0,
            Code = "bad code",
            Name = "",
            ZoneType = new string('a', 21),
            Capacity = -1,
            MaxReservationSlots = -2
        };

        var result = new CreateZoneModelValidator().TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.ParkingLotId);
        result.ShouldHaveValidationErrorFor(x => x.Code);
        result.ShouldHaveValidationErrorFor(x => x.Name);
        result.ShouldHaveValidationErrorFor(x => x.ZoneType);
        result.ShouldHaveValidationErrorFor(x => x.Capacity);
        result.ShouldHaveValidationErrorFor(x => x.MaxReservationSlots);
    }

    [Fact]
    public void CreateZone_Should_Reject_More_Reservation_Slots_Than_Capacity()
    {
        var model = new CreateZoneModel { ParkingLotId = 1, Name = "Z", Capacity = 5, MaxReservationSlots = 6 };

        new CreateZoneModelValidator().TestValidate(model)
            .ShouldHaveValidationErrorFor(x => x.MaxReservationSlots)
            .WithErrorMessage("Max reservation slots cannot exceed zone capacity.");
    }

    [Fact]
    public void UpdateZone_Should_Accept_A_Valid_Zone_And_Reject_Bad_Values()
    {
        var validator = new UpdateZoneModelValidator();

        validator.TestValidate(new UpdateZoneModel { Name = "Z", Capacity = 10, MaxReservationSlots = 10 })
            .ShouldNotHaveAnyValidationErrors();
        validator.TestValidate(new UpdateZoneModel { Name = "Z", Capacity = 10 })
            .ShouldNotHaveAnyValidationErrors();

        var result = validator.TestValidate(new UpdateZoneModel
        {
            Name = "",
            Capacity = -1,
            ZoneType = new string('a', 21),
            MaxReservationSlots = 3
        });

        result.ShouldHaveValidationErrorFor(x => x.Name);
        result.ShouldHaveValidationErrorFor(x => x.Capacity);
        result.ShouldHaveValidationErrorFor(x => x.ZoneType);
        result.ShouldHaveValidationErrorFor(x => x.MaxReservationSlots);
    }

    // ---- Slots ----

    [Theory]
    [InlineData("Available")]
    [InlineData("maintenance")]
    [InlineData(null)]
    public void CreateSlot_Should_Accept_Known_Statuses(string status)
    {
        var model = new CreateSlotModel { ZoneId = 1, VehicleTypeId = 1, Code = "A-01", Status = status };

        new CreateSlotModelValidator().TestValidate(model).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void CreateSlot_Should_Reject_Invalid_Values()
    {
        var model = new CreateSlotModel
        {
            ZoneId = 0,
            VehicleTypeId = 0,
            Code = "A 01",
            Status = "Broken",
            DedicatedNote = new string('a', 201)
        };

        var result = new CreateSlotModelValidator().TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.ZoneId);
        result.ShouldHaveValidationErrorFor(x => x.VehicleTypeId);
        result.ShouldHaveValidationErrorFor(x => x.Code);
        result.ShouldHaveValidationErrorFor(x => x.Status);
        result.ShouldHaveValidationErrorFor(x => x.DedicatedNote);
    }

    [Fact]
    public void UpdateSlot_Should_Accept_Partial_Updates_And_Reject_Bad_Values()
    {
        var validator = new UpdateSlotModelValidator();

        validator.TestValidate(new UpdateSlotModel()).ShouldNotHaveAnyValidationErrors();
        validator.TestValidate(new UpdateSlotModel { VehicleTypeId = 2, Code = "B-02" }).ShouldNotHaveAnyValidationErrors();

        var result = validator.TestValidate(new UpdateSlotModel
        {
            VehicleTypeId = 0,
            Code = new string('a', 30) + "!",
            DedicatedNote = new string('a', 201)
        });

        result.ShouldHaveValidationErrorFor(x => x.VehicleTypeId);
        result.ShouldHaveValidationErrorFor(x => x.Code);
        result.ShouldHaveValidationErrorFor(x => x.DedicatedNote);
    }
}
