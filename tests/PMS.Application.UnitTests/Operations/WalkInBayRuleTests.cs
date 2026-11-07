using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>
/// Where a walk-in may park (ParkingInventory.WalkInBaysAsync, used by the entry lane, the simulation and the map):
/// never a bay with a vehicle in it or a booking due soon, and a bay booked for later only when no other is left.
/// </summary>
public class WalkInBayRuleTests
{
    private static readonly TimeSpan HoldAhead = TimeSpan.FromMinutes(120);

    /// <summary>A second car of the customer, so two bookings can be made (one per vehicle).</summary>
    private static async Task<Vehicle> SecondCarAsync(OperationsTestContext t)
    {
        var car = new Vehicle
        {
            CustomerId = t.Customer.Id, VehicleTypeId = t.TypeId("CAR_4"), PlateNumber = "51A-222.33", PlateNormalized = "51A22233",
            IsActive = true, CreatedAt = t.Now
        };
        t.Db.Vehicles.Add(car);
        t.ApprovePapers(car);
        await t.Db.SaveChangesAsync();
        return car;
    }

    private static Task<ReservationResponseModel> BookAsync(OperationsTestContext t, Vehicle vehicle, Slot bay, int arrivalInMinutes) =>
        t.Reservations.CreateAsync(t.CustomerUserId, new CreateReservationModel
        {
            ParkingLotId = t.Lot.Id, SlotId = bay.Id, VehicleId = vehicle.Id,
            ArrivalTime = t.Clock.GetUtcNow().AddMinutes(arrivalInMinutes), PayDeposit = false
        });

    private static CheckInModel WalkIn(OperationsTestContext t, int bayId, string plate = "51G-555.66") => new()
    {
        PlateNumber = plate, VehicleTypeId = t.TypeId("CAR_4"), PlateConfirmed = true, IgnoreReservation = true, SlotId = bayId
    };

    /// <summary>One bay booked within the hold window (held now), one booked for much later (not held yet).</summary>
    private static async Task<(Slot Soon, Slot Later)> BookTwoBaysAsync(OperationsTestContext t)
    {
        var soon = t.FreeCarSlot(0);
        var later = t.FreeCarSlot(1);
        await BookAsync(t, t.Car, soon, arrivalInMinutes: 60);
        await BookAsync(t, await SecondCarAsync(t), later, arrivalInMinutes: 300);
        return (soon, later);
    }

    [Fact]
    public async Task Bays_Without_A_Booking_Come_First_And_A_Bay_Booked_Soon_Never_Shows()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (soon, later) = await BookTwoBaysAsync(t);

        var bays = await t.Inventory.WalkInBaysAsync(t.Lot.Id, t.TypeId("CAR_4"), t.Now, HoldAhead);

        bays.Should().NotContain(b => b.Slot.Id == soon.Id, "its booking is held");
        bays.Should().Contain(b => b.Slot.Id == later.Id);
        bays.First().NextBookingAt.Should().BeNull();
        bays.Last().Slot.Id.Should().Be(later.Id, "a bay booked for later is the last choice");
    }

    [Fact]
    public async Task The_Lane_Refuses_A_Booked_Or_Taken_Bay_And_Accepts_A_Bay_Without_A_Booking()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (soon, later) = await BookTwoBaysAsync(t);

        var held = await t.Gates.CheckInAsync(t.Lot.Id, WalkIn(t, soon.Id));
        held.Outcome.Should().Be("Blocked");
        held.Alerts.Should().Contain(a => a.Code == "SLOT_UNAVAILABLE" && a.Blocking);

        var bookedLater = await t.Gates.CheckInAsync(t.Lot.Id, WalkIn(t, later.Id));
        bookedLater.Outcome.Should().Be("Blocked");
        bookedLater.Alerts.Should().Contain(a => a.Code == "BAY_BOOKED_LATER" && a.Blocking);

        var free = (await t.Inventory.WalkInBaysAsync(t.Lot.Id, t.TypeId("CAR_4"), t.Now, HoldAhead)).First(b => b.NextBookingAt == null).Slot;
        var entry = await t.Gates.CheckInAsync(t.Lot.Id, WalkIn(t, free.Id));
        entry.Outcome.Should().Be("Created");
        await t.Gates.ConfirmEntryAsync(entry.Session.Id);

        // Now taken: no second walk-in may park there
        (await t.Inventory.WalkInBaysAsync(t.Lot.Id, t.TypeId("CAR_4"), t.Now, HoldAhead)).Should().NotContain(b => b.Slot.Id == free.Id);
        var second = await t.Gates.CheckInAsync(t.Lot.Id, WalkIn(t, free.Id, "51G-777.88"));
        second.Outcome.Should().Be("Blocked");
        second.Alerts.Should().Contain(a => a.Code == "SLOT_UNAVAILABLE");
    }

    [Fact]
    public async Task A_Bay_Booked_For_Later_Is_Used_Only_When_No_Other_Is_Left_With_A_Warning()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (_, later) = await BookTwoBaysAsync(t);
        var cars = new[] { t.TypeId("CAR_4"), t.TypeId("CAR_7") };
        foreach (var bay in t.Db.Slots.Where(s => cars.Contains(s.VehicleTypeId) && s.Id != later.Id && s.Status == SlotStatus.Available))
            bay.Status = SlotStatus.Maintenance;
        await t.Db.SaveChangesAsync();

        var entry = await t.Gates.CheckInAsync(t.Lot.Id, WalkIn(t, later.Id));

        entry.Outcome.Should().Be("Created");
        entry.Alerts.Should().Contain(a => a.Code == "BAY_BOOKED_LATER" && !a.Blocking);
    }
}
