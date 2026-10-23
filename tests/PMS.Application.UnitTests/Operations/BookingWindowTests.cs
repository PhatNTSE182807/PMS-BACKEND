using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>
/// Booking by date and time (Scheduled windows and pay-as-you-go), the bay lock shortly before arrival,
/// the customer's current session and the exit pass every parked vehicle gets.
/// </summary>
public class BookingWindowTests
{
    /// <summary>A second approved car of the same customer (an account may hold two bookings).</summary>
    private static async Task<Vehicle> SecondCarAsync(OperationsTestContext t)
    {
        var car = new Vehicle { CustomerId = t.Customer.Id, VehicleTypeId = t.Car.VehicleTypeId, PlateNumber = "51B-222.22", PlateNormalized = "51B22222", IsActive = true, CreatedAt = t.Now };
        t.Db.Vehicles.Add(car);
        await t.Db.SaveChangesAsync();
        t.ApprovePapers(car);
        await t.Db.SaveChangesAsync();
        return car;
    }

    private static CreateReservationModel Scheduled(OperationsTestContext t, Vehicle car, int slotId, TimeSpan arrivalIn, TimeSpan stay) => new()
    {
        ParkingLotId = t.Lot.Id,
        SlotId = slotId,
        VehicleId = car.Id,
        BookingMode = BookingMode.Scheduled,
        ArrivalTime = t.Clock.GetUtcNow().Add(arrivalIn),
        DepartureTime = t.Clock.GetUtcNow().Add(arrivalIn + stay)
    };

    private static CreateReservationModel PayAsYouGo(OperationsTestContext t, Vehicle car, int slotId, TimeSpan arrivalIn) => new()
    {
        ParkingLotId = t.Lot.Id,
        SlotId = slotId,
        VehicleId = car.Id,
        BookingMode = BookingMode.PayAsYouGo,
        ArrivalTime = t.Clock.GetUtcNow().Add(arrivalIn)
    };

    [Fact]
    public async Task A_Booking_Days_Ahead_Keeps_The_Bay_Free_Until_Shortly_Before_Arrival()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var slot = t.FreeCarSlot();

        var booked = await t.Reservations.CreateAsync(t.CustomerUserId, Scheduled(t, t.Car, slot.Id, TimeSpan.FromDays(3), TimeSpan.FromHours(4)));

        booked.BookingMode.Should().Be(BookingMode.Scheduled);
        booked.DepartureTime.Should().Be(booked.ArrivalTime.AddHours(4));
        booked.IsSlotHeld.Should().BeFalse();
        t.Db.Slots.Single(s => s.Id == slot.Id).Status.Should().Be(SlotStatus.Available);
    }

    [Fact]
    public async Task A_Booking_Close_To_Arrival_Locks_The_Bay_Right_Away()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var slot = t.FreeCarSlot();

        var booked = await t.Reservations.CreateAsync(t.CustomerUserId, PayAsYouGo(t, t.Car, slot.Id, TimeSpan.FromMinutes(45)));

        booked.BookingMode.Should().Be(BookingMode.PayAsYouGo);
        booked.DepartureTime.Should().BeNull();
        booked.IsSlotHeld.Should().BeTrue();
        t.Db.Slots.Single(s => s.Id == slot.Id).Status.Should().Be(SlotStatus.Reserved);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(25 * 60)]
    public async Task A_Scheduled_Stay_Must_Be_Between_The_Minimum_And_The_Maximum(int stayMinutes)
    {
        var t = await new OperationsTestContext().SeedAsync();

        var act = () => t.Reservations.CreateAsync(t.CustomerUserId,
            Scheduled(t, t.Car, t.FreeCarSlot().Id, TimeSpan.FromDays(1), TimeSpan.FromMinutes(stayMinutes)));

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_Scheduled_Booking_Needs_A_Departure()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var model = Scheduled(t, t.Car, t.FreeCarSlot().Id, TimeSpan.FromDays(1), TimeSpan.FromHours(2));
        model.DepartureTime = null;

        var act = () => t.Reservations.CreateAsync(t.CustomerUserId, model);

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*departure*");
    }

    [Fact]
    public async Task One_Bay_Takes_Bookings_At_Different_Times_But_Not_Overlapping_Ones()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var second = await SecondCarAsync(t);
        var slot = t.FreeCarSlot();
        await t.Reservations.CreateAsync(t.CustomerUserId, Scheduled(t, t.Car, slot.Id, TimeSpan.FromDays(1), TimeSpan.FromHours(3)));

        var overlapping = () => t.Reservations.CreateAsync(t.CustomerUserId,
            Scheduled(t, second, slot.Id, TimeSpan.FromDays(1) + TimeSpan.FromHours(2), TimeSpan.FromHours(2)));
        await overlapping.Should().ThrowAsync<ConflictException>();

        // Starts after the first window ends (plus the early check-in margin)
        var later = await t.Reservations.CreateAsync(t.CustomerUserId,
            Scheduled(t, second, slot.Id, TimeSpan.FromDays(1) + TimeSpan.FromHours(4), TimeSpan.FromHours(2)));
        later.SlotId.Should().Be(slot.Id);
    }

    [Fact]
    public async Task Pay_As_You_Go_Keeps_The_Bay_From_Arrival_On()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var second = await SecondCarAsync(t);
        var slot = t.FreeCarSlot();
        await t.Reservations.CreateAsync(t.CustomerUserId, PayAsYouGo(t, t.Car, slot.Id, TimeSpan.FromDays(1)));

        var dayAfter = () => t.Reservations.CreateAsync(t.CustomerUserId,
            Scheduled(t, second, slot.Id, TimeSpan.FromDays(2), TimeSpan.FromHours(2)));
        await dayAfter.Should().ThrowAsync<ConflictException>();

        var dayBefore = await t.Reservations.CreateAsync(t.CustomerUserId,
            Scheduled(t, second, slot.Id, TimeSpan.FromHours(5), TimeSpan.FromHours(2)));
        dayBefore.SlotId.Should().Be(slot.Id);
    }

    [Fact]
    public async Task The_Job_Locks_The_Bay_Before_Arrival_And_Moves_The_Booking_When_The_Bay_Is_Taken()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var slot = t.FreeCarSlot();
        var booked = await t.Reservations.CreateAsync(t.CustomerUserId, Scheduled(t, t.Car, slot.Id, TimeSpan.FromHours(6), TimeSpan.FromHours(2)));

        // Nothing to do yet
        (await t.Reservations.ActivateDueHoldsAsync()).Should().Be(0);

        // Meanwhile another car parks on that bay
        t.Db.Slots.Single(s => s.Id == slot.Id).Status = SlotStatus.Occupied;
        await t.Db.SaveChangesAsync();

        t.Clock.Advance(TimeSpan.FromHours(4.5));
        (await t.Reservations.ActivateDueHoldsAsync()).Should().Be(1);

        var moved = await t.Reservations.GetAsync(t.CustomerUserId, booked.Id);
        moved.IsSlotHeld.Should().BeTrue();
        moved.SlotId.Should().NotBe(slot.Id);
        t.Db.Slots.Single(s => s.Id == moved.SlotId).Status.Should().Be(SlotStatus.Reserved);
    }

    [Fact]
    public async Task Cancelling_A_Booking_That_Did_Not_Lock_The_Bay_Leaves_The_Bay_Alone()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var second = await SecondCarAsync(t);
        var slot = t.FreeCarSlot();
        var soon = await t.Reservations.CreateAsync(t.CustomerUserId, Scheduled(t, t.Car, slot.Id, TimeSpan.FromMinutes(40), TimeSpan.FromHours(1)));
        var later = await t.Reservations.CreateAsync(t.CustomerUserId, Scheduled(t, second, slot.Id, TimeSpan.FromDays(1), TimeSpan.FromHours(1)));

        await t.Reservations.CancelAsync(t.CustomerUserId, later.Id, "changed plans");

        soon.IsSlotHeld.Should().BeTrue();
        t.Db.Slots.Single(s => s.Id == slot.Id).Status.Should().Be(SlotStatus.Reserved);
    }

    [Fact]
    public async Task Changing_The_Window_Switches_The_Mode_And_Releases_The_Lock_When_It_Moves_Far_Ahead()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (confirmed, _) = await t.ConfirmedReservationAsync(arrivalInMinutes: 90);
        confirmed.IsSlotHeld.Should().BeTrue();

        var changed = await t.Reservations.UpdateAsync(t.CustomerUserId, confirmed.Id, new UpdateReservationModel
        {
            ArrivalTime = t.Clock.GetUtcNow().AddDays(2),
            BookingMode = BookingMode.Scheduled,
            DepartureTime = t.Clock.GetUtcNow().AddDays(2).AddHours(3)
        });

        changed.BookingMode.Should().Be(BookingMode.Scheduled);
        changed.DepartureTime.Should().Be(changed.ArrivalTime.AddHours(3));
        changed.IsSlotHeld.Should().BeFalse();
        t.Db.Slots.Single(s => s.Id == confirmed.SlotId).Status.Should().Be(SlotStatus.Available);
    }

    [Fact]
    public async Task Upcoming_Is_The_Soonest_Booking_That_Has_Not_Started()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var second = await SecondCarAsync(t);
        await t.Reservations.CreateAsync(t.CustomerUserId, PayAsYouGo(t, t.Car, t.FreeCarSlot().Id, TimeSpan.FromDays(2)));
        var soonest = await t.Reservations.CreateAsync(t.CustomerUserId, PayAsYouGo(t, second, t.FreeCarSlot(1).Id, TimeSpan.FromHours(5)));

        var upcoming = await t.Reservations.GetUpcomingAsync(t.CustomerUserId);

        upcoming!.Id.Should().Be(soonest.Id);
        upcoming.ParkingLotName.Should().Be(t.Lot.Name);
        (await t.Reservations.GetUpcomingAsync("someone-else")).Should().BeNull();
    }

    [Fact]
    public async Task A_Parked_Car_Has_A_Current_Session_With_An_Exit_Pass_The_Exit_Lane_Accepts()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (reservation, _) = await t.ConfirmedReservationAsync(arrivalInMinutes: 5);
        var entry = await t.Gates.CheckInAsync(t.Lot.Id, new CheckInModel { PlateNumber = t.Car.PlateNumber, ReservationCode = reservation.Code });
        await t.Gates.ConfirmEntryAsync(entry.Session.Id);
        t.Clock.Advance(TimeSpan.FromMinutes(95));

        var current = (await t.Gates.GetMyCurrentSessionsAsync(t.CustomerUserId)).Single();

        current.Session.Id.Should().Be(entry.Session.Id);
        current.LotName.Should().Be(t.Lot.Name);
        current.EntryMethod.Should().Be("Reservation");
        current.ElapsedMinutes.Should().Be(95);
        current.FeeSoFar.Should().BeGreaterThan(0);
        current.Tariff.BlockPrice.Should().BeGreaterThan(0);
        current.ExitPass.Prepaid.Should().BeFalse();
        current.ExitPass.AmountDue.Should().Be(current.AmountDue);
        t.Signer.Verify(current.ExitPass.Token, ParkingTokenSigner.ExitPassKind)!["sc"].Should().Be(entry.Session.Code);

        // The exit lane finds the session from the pass, like an entry ticket
        var found = await t.Gates.LookupAsync(t.Lot.Id, null, current.ExitPass.Token, null);
        found.Id.Should().Be(entry.Session.Id);

        // Looking at the fee does not change the session
        (await t.Gates.GetMyExitPassAsync(t.CustomerUserId, entry.Session.Id)).SessionCode.Should().Be(entry.Session.Code);
        t.Db.ParkingSessions.Single(s => s.Id == entry.Session.Id).Status.Should().Be(SessionStatus.Active);
    }
}
