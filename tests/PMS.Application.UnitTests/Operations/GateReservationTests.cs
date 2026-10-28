using System.Threading.Tasks;
using FluentAssertions;
using PMS.Application.Exceptions;
using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>One booking and one parked session per vehicle, and the entry lane's reservation lookup.</summary>
public class GateReservationTests
{
    private static CreateReservationModel Booking(OperationsTestContext t, Vehicle vehicle, int skipSlots = 1) => new()
    {
        ParkingLotId = t.Lot.Id,
        SlotId = t.FreeCarSlot(skipSlots).Id,
        VehicleId = vehicle.Id,
        ArrivalTime = t.Clock.GetUtcNow().AddMinutes(30)
    };

    /// <summary>Checks the booked car in on its reservation code and lets the loop sensor confirm it.</summary>
    private static async Task CheckInAsync(OperationsTestContext t, string plate, string code)
    {
        var result = await t.Gates.CheckInAsync(t.Lot.Id, new CheckInModel { PlateNumber = plate, ReservationCode = code });
        result.Outcome.Should().Be("Created");
        await t.Gates.ConfirmEntryAsync(result.Session.Id);
    }

    [Fact]
    public async Task A_Parked_Car_Cannot_Book_Again_Until_It_Leaves()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (reservation, _) = await t.ConfirmedReservationAsync(arrivalInMinutes: 5);
        await CheckInAsync(t, t.Car.PlateNumber, reservation.Code);

        var act = () => t.Reservations.CreateAsync(t.CustomerUserId, Booking(t, t.Car));

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*already has an active reservation*");
    }

    [Fact]
    public async Task The_Same_Plate_Added_Twice_Gets_Only_One_Booking()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var copy = new Vehicle { CustomerId = t.Customer.Id, VehicleTypeId = t.Car.VehicleTypeId, PlateNumber = "51A-123.45", PlateNormalized = "51A12345", IsActive = true };
        t.Db.Vehicles.Add(copy);
        t.ApprovePapers(copy);
        await t.Db.SaveChangesAsync();
        await t.Reservations.CreateAsync(t.CustomerUserId, Booking(t, t.Car, skipSlots: 0));

        var act = () => t.Reservations.CreateAsync(t.CustomerUserId, Booking(t, copy));

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*already has an active reservation*");
    }

    [Fact]
    public async Task A_Reservation_Checks_In_Once()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (reservation, _) = await t.ConfirmedReservationAsync(arrivalInMinutes: 5);
        await CheckInAsync(t, t.Car.PlateNumber, reservation.Code);

        // Same code again, even with another plate typed in
        var again = await t.Gates.CheckInAsync(t.Lot.Id, new CheckInModel { PlateNumber = "30X-000.01", ReservationCode = reservation.Code, AllowPlateMismatch = true, OverrideReason = "test" });

        again.Outcome.Should().Be("Blocked");
        again.Alerts.Should().Contain(a => a.Code == "RESERVATION_INVALID");
    }

    [Fact]
    public async Task A_Booked_Car_Already_Parked_As_A_Walk_In_Cannot_Enter_Again_Under_Another_Plate()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (reservation, _) = await t.ConfirmedReservationAsync(arrivalInMinutes: 5);
        var walkIn = await t.Gates.CheckInAsync(t.Lot.Id, new CheckInModel { PlateNumber = t.Car.PlateNumber, VehicleTypeId = t.Car.VehicleTypeId, IgnoreReservation = true });
        walkIn.Outcome.Should().Be("Created");

        var second = await t.Gates.CheckInAsync(t.Lot.Id, new CheckInModel { PlateNumber = "30X-000.01", ReservationCode = reservation.Code, AllowPlateMismatch = true, OverrideReason = "test" });

        second.Outcome.Should().Be("Blocked");
        second.Alerts.Should().Contain(a => a.Code == "DUPLICATE_SESSION");
    }

    [Fact]
    public async Task Lookup_By_Code_Returns_The_Booked_Vehicle_And_Whether_It_Can_Enter()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (reservation, _) = await t.ConfirmedReservationAsync(arrivalInMinutes: 5);

        var found = await t.Reservations.LookupForGateAsync(t.Lot.Id, reservation.Code.ToLowerInvariant(), null);

        found.Reservation.PlateNumber.Should().Be("51A-123.45");
        found.VehicleTypeId.Should().Be(t.Car.VehicleTypeId);
        found.VehicleTypeName.Should().Be("4-Seat Car");
        found.CustomerName.Should().Be("Customer");
        found.CanCheckIn.Should().BeTrue();

        await CheckInAsync(t, t.Car.PlateNumber, reservation.Code);
        var after = await t.Reservations.LookupForGateAsync(t.Lot.Id, reservation.Code, null);
        after.CanCheckIn.Should().BeFalse();
    }

    [Theory]
    [InlineData("51A-123.45")]
    [InlineData("51a12345")]
    [InlineData(" 51A 123 45 ")]
    public async Task Lookup_By_Plate_Returns_The_Reservation_Code_Whatever_The_Plate_Format(string typed)
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (reservation, _) = await t.ConfirmedReservationAsync(arrivalInMinutes: 5);

        var found = await t.Reservations.LookupForGateAsync(t.Lot.Id, null, null, typed);

        found.Reservation.Code.Should().Be(reservation.Code);
        found.CanCheckIn.Should().BeTrue();
    }

    [Fact]
    public async Task Lookup_By_Plate_Without_A_Booking_Is_Not_Found()
    {
        var t = await new OperationsTestContext().SeedAsync();

        var act = () => t.Reservations.LookupForGateAsync(t.Lot.Id, null, null, "30X-000.01");

        await act.Should().ThrowAsync<NotFoundException>().WithMessage("*plate*");
    }

    [Fact]
    public async Task Lookup_Without_Code_QR_Or_Plate_Is_Refused()
    {
        var t = await new OperationsTestContext().SeedAsync();

        var act = () => t.Reservations.LookupForGateAsync(t.Lot.Id, null, null, null);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Lookup_Says_Too_Early_Before_The_Window()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (reservation, _) = await t.ConfirmedReservationAsync(arrivalInMinutes: 90);

        var found = await t.Reservations.LookupForGateAsync(t.Lot.Id, reservation.Code, null);

        found.CanCheckIn.Should().BeFalse();
        found.Problem.Should().Contain("Too early");
    }
}
