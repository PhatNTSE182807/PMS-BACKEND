using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using PMS.Application.Exceptions;
using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>The 3D lot model's bay popup for Staff, Managers and Admins (GetBaySessionAsync).</summary>
public class BaySessionTests
{
    private static ParkingSession Parked(OperationsTestContext t, Slot slot, int? customerId, string status, int minutesAgo) => new()
    {
        SessionCode = $"BAY-{slot.Id}-{status}", ParkingLotId = t.Lot.Id, CustomerId = customerId, EntryGateId = 1,
        EntryTime = t.Now.AddMinutes(-minutesAgo), EntryPlateNumber = "51A-123.45", EntryPlateNormalized = "51A12345",
        ZoneId = slot.ZoneId, SlotId = slot.Id, VehicleTypeId = t.TypeId("CAR_4"), Status = status,
        SessionType = customerId == null ? SessionType.WalkIn : SessionType.Reservation, RowVersion = [1]
    };

    [Fact]
    public async Task Member_In_Bay_Shows_Name_Plate_Time_And_Fee_So_Far()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var slot = t.FreeCarSlot();
        t.Db.ParkingSessions.Add(Parked(t, slot, t.Customer.Id, SessionStatus.Active, 95));
        await t.Db.SaveChangesAsync();

        var bay = await t.Gates.GetBaySessionAsync(t.Lot.Id, slot.Id);

        bay.CustomerType.Should().Be("Member");
        bay.CustomerName.Should().Be("Customer");
        bay.Session.PlateNumber.Should().Be("51A-123.45");
        bay.Session.SlotId.Should().Be(slot.Id);
        bay.ElapsedMinutes.Should().Be(95);
        bay.FeeSoFar.Should().NotBeNull().And.BeGreaterThan(0);
        bay.AmountDue.Should().NotBeNull();
    }

    [Fact]
    public async Task Walk_In_Is_A_Guest_Without_A_Name()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var slot = t.FreeCarSlot();
        t.Db.ParkingSessions.Add(Parked(t, slot, null, SessionStatus.Active, 10));
        await t.Db.SaveChangesAsync();

        var bay = await t.Gates.GetBaySessionAsync(t.Lot.Id, slot.Id);

        bay.CustomerType.Should().Be("Guest");
        bay.CustomerName.Should().BeNull();
    }

    [Fact]
    public async Task Vehicle_Still_At_The_Barrier_Has_No_Fee_Yet()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var slot = t.FreeCarSlot();
        t.Db.ParkingSessions.Add(Parked(t, slot, null, SessionStatus.PendingEntry, 0));
        await t.Db.SaveChangesAsync();

        var bay = await t.Gates.GetBaySessionAsync(t.Lot.Id, slot.Id);

        bay.FeeSoFar.Should().BeNull();
        bay.AmountDue.Should().BeNull();
    }

    [Fact]
    public async Task Empty_Bay_Or_Finished_Session_Is_Not_Found()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var slot = t.FreeCarSlot();
        t.Db.ParkingSessions.Add(Parked(t, slot, null, SessionStatus.Completed, 120));
        await t.Db.SaveChangesAsync();

        await t.Gates.Invoking(g => g.GetBaySessionAsync(t.Lot.Id, slot.Id)).Should().ThrowAsync<NotFoundException>();
        await t.Gates.Invoking(g => g.GetBaySessionAsync(t.Lot.Id, t.FreeCarSlot(1).Id)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Staff_Without_Lot_Access_Is_Refused()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var slot = t.FreeCarSlot();
        t.Db.ParkingSessions.Add(Parked(t, slot, t.Customer.Id, SessionStatus.Active, 30));
        await t.Db.SaveChangesAsync();
        t.ActAs("other-staff", "Staff");

        await t.Gates.Invoking(g => g.GetBaySessionAsync(t.Lot.Id, slot.Id)).Should().ThrowAsync<ForbiddenException>();
    }
}
