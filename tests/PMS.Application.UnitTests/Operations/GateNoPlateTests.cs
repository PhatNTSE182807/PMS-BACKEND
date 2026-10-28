using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.Operations;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>A vehicle without a plate needs a reason; a typed plate can never be entered as plate-less.</summary>
public class GateNoPlateTests
{
    private static CheckInModel NoPlate(OperationsTestContext t, string reason = "Plate torn off", string plate = null) => new()
    {
        NoPlate = true,
        NoPlateReason = reason,
        PlateNumber = plate,
        VehicleTypeId = t.Car.VehicleTypeId
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_Plate_Less_Entry_Needs_A_Reason(string reason)
    {
        var t = await new OperationsTestContext().SeedAsync();

        var act = () => t.Gates.CheckInAsync(t.Lot.Id, NoPlate(t, reason));

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*reason*no plate*");
    }

    [Fact]
    public async Task A_Plate_Less_Entry_Keeps_Its_Reason_And_Is_Not_A_Manual_Entry()
    {
        var t = await new OperationsTestContext().SeedAsync();

        var result = await t.Gates.CheckInAsync(t.Lot.Id, NoPlate(t, "  Plate torn off  "));

        result.Outcome.Should().Be("Created");
        var saved = await t.Db.ParkingSessions.AsNoTracking().SingleAsync(s => s.Id == result.Session.Id);
        saved.HasPlate.Should().BeFalse();
        saved.NoPlateReason.Should().Be("Plate torn off");
        saved.IsManualEntry.Should().BeFalse();
    }

    [Fact]
    public async Task A_Typed_Plate_Cannot_Be_Entered_As_Plate_Less()
    {
        var t = await new OperationsTestContext().SeedAsync();

        var act = () => t.Gates.CheckInAsync(t.Lot.Id, NoPlate(t, plate: t.Car.PlateNumber));

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*plate is entered*");
    }

    [Fact]
    public async Task A_Reason_Is_Ignored_For_A_Vehicle_With_A_Plate()
    {
        var t = await new OperationsTestContext().SeedAsync();

        var result = await t.Gates.CheckInAsync(t.Lot.Id, new CheckInModel { PlateNumber = t.Car.PlateNumber, NoPlateReason = "stray", VehicleTypeId = t.Car.VehicleTypeId });

        var saved = await t.Db.ParkingSessions.AsNoTracking().SingleAsync(s => s.Id == result.Session.Id);
        saved.NoPlateReason.Should().BeNull();
    }
}
