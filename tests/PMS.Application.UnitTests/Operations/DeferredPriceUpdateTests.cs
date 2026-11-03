using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using PMS.Application.Models.Pricing;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>
/// A Manager's new price is live at once only when the lot is empty; otherwise it waits for the last vehicle to
/// leave. Stays and bookings keep the price they started with. (Real races need SQL Server's rowversion and are
/// tested by hand; here only the retry loop around them is exercised.)
/// </summary>
public class DeferredPriceUpdateTests
{
    private const decimal OldPrice = 20_000;
    private const decimal NewPrice = 30_000;
    private const string PlateA = "51A-123.45";
    private const string PlateB = "51B-222.33";

    private sealed class Setup
    {
        public OperationsTestContext T { get; init; }
        public PricingRuleService Service { get; init; }
        public ILotPriceNotifier Notifier { get; init; }
        public PricingRule Rule { get; init; }

        public async Task<ParkingLot> LotAsync() => await T.Db.ParkingLots.AsNoTracking().SingleAsync();
    }

    private static async Task<Setup> NewSetupAsync()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var rule = new PricingRule
        {
            Code = "R-CAR4", Name = "Car", ParkingLotId = t.Lot.Id, VehicleTypeId = t.TypeId("CAR_4"),
            ChargeModel = "Block", BlockMinutes = 60, BlockPrice = OldPrice, GraceMinutes = 15, Currency = "VND",
            Status = "Active", VersionNo = 1, EffectiveFrom = t.Now.AddDays(-1), CreatedAt = t.Now
        };
        t.Db.PricingRules.Add(rule);
        await t.Db.SaveChangesAsync();

        var notifier = Substitute.For<ILotPriceNotifier>();
        var service = new PricingRuleService(t.Db, new LotScopeService(t.Db, t.Claims), new TariffProvider(t.Db, t.Options), t.Prices, t.Options, t.Clock, notifier);
        t.ActAs(t.ManagerUserId, "Manager");
        return new Setup { T = t, Service = service, Notifier = notifier, Rule = rule };
    }

    private static UpdatePricingRuleModel Update(PricingRule rule, decimal blockPrice) => new()
    {
        Name = rule.Name, VehicleTypeId = rule.VehicleTypeId, ChargeModel = "Block", BlockMinutes = 60, BlockPrice = blockPrice,
        GraceMinutes = 15, Priority = rule.Priority, EffectiveFrom = rule.EffectiveFrom, Status = "Active", Reason = "test"
    };

    private static async Task<long> ParkAsync(OperationsTestContext t, string plate)
    {
        t.ActAs(t.StaffUserId, "Staff");
        var entry = await t.Gates.CheckInAsync(t.Lot.Id, new CheckInModel { PlateNumber = plate, VehicleTypeId = t.TypeId("CAR_4") });
        entry.Outcome.Should().Be("Created");
        await t.Gates.ConfirmEntryAsync(entry.Session.Id);
        t.ActAs(t.ManagerUserId, "Manager");
        return entry.Session.Id;
    }

    private static async Task LeaveAsync(OperationsTestContext t, long sessionId, string plate)
    {
        t.ActAs(t.StaffUserId, "Staff");
        t.Clock.Advance(TimeSpan.FromMinutes(10));
        var due = await t.Gates.RequestPaymentAsync(sessionId, new CheckoutQuoteRequestModel());
        await t.Gates.PayCashAsync(sessionId, new CashPaymentModel { Amount = due.AmountDue });
        var left = await t.Gates.CheckoutAsync(sessionId, new CheckoutModel { PlateNumber = plate });
        left.BarrierOpen.Should().BeTrue();
        t.ActAs(t.ManagerUserId, "Manager");
    }

    private async Task<decimal> LivePriceAsync(Setup s) =>
        (await s.T.Db.PricingRules.AsNoTracking().SingleAsync(r => r.Id == s.Rule.Id)).BlockPrice!.Value;

    // ---- Manager updates the price ----------------------------------------------------------------

    [Fact]
    public async Task An_Empty_Lot_Takes_The_New_Price_At_Once()
    {
        var s = await NewSetupAsync();

        var result = await s.Service.UpdatePricingRuleAsync(s.T.Lot.Id, s.Rule.Id, Update(s.Rule, NewPrice));

        result.PriceUpdateStatus.Should().Be("Applied");
        result.BlockPrice.Should().Be(NewPrice);
        result.VersionNo.Should().Be(2);
        result.PendingPrice.Should().BeNull();
        s.T.Db.PendingPriceChanges.Should().BeEmpty();
        await s.Notifier.Received(1).PriceAppliedAsync(Arg.Is<LotPriceNotice>(n => n.AppliedMessage.Contains("30,000 VND per 60 min")));
        await s.Notifier.DidNotReceive().PriceScheduledAsync(Arg.Any<LotPriceNotice>());
    }

    [Fact]
    public async Task An_Occupied_Lot_Keeps_The_Price_And_Stores_The_New_One()
    {
        var s = await NewSetupAsync();
        await ParkAsync(s.T, PlateA);

        var result = await s.Service.UpdatePricingRuleAsync(s.T.Lot.Id, s.Rule.Id, Update(s.Rule, NewPrice));

        result.PriceUpdateStatus.Should().Be("Pending");
        result.BlockPrice.Should().Be(OldPrice, "the live price does not move while a vehicle is inside");
        result.VersionNo.Should().Be(1);
        result.PendingPrice.Prices.BlockPrice.Should().Be(NewPrice);
        result.PendingPrice.Summary.Should().Be("30,000 VND per 60 min");
        (await LivePriceAsync(s)).Should().Be(OldPrice);
        s.T.Db.PendingPriceChanges.Should().ContainSingle(p => p.PricingRuleId == s.Rule.Id && p.RevisionNo == 1);
        await s.Notifier.Received(1).PriceScheduledAsync(Arg.Is<LotPriceNotice>(n => n.ScheduledMessage.Contains("once the lot is empty")));
        await s.Notifier.DidNotReceive().PriceAppliedAsync(Arg.Any<LotPriceNotice>());
    }

    [Fact]
    public async Task Another_Request_Before_The_Lot_Empties_Overwrites_The_Waiting_Price()
    {
        var s = await NewSetupAsync();
        await ParkAsync(s.T, PlateA);
        await s.Service.UpdatePricingRuleAsync(s.T.Lot.Id, s.Rule.Id, Update(s.Rule, 15_000));

        var second = await s.Service.UpdatePricingRuleAsync(s.T.Lot.Id, s.Rule.Id, Update(s.Rule, 12_000));

        second.PriceUpdateStatus.Should().Be("Pending");
        var waiting = s.T.Db.PendingPriceChanges.Single();
        waiting.RevisionNo.Should().Be(2);
        waiting.UpdatedAt.Should().NotBeNull();
        second.PendingPrice.Prices.BlockPrice.Should().Be(12_000);
    }

    [Fact]
    public async Task Asking_For_The_Current_Price_Again_Withdraws_The_Waiting_Change()
    {
        var s = await NewSetupAsync();
        await ParkAsync(s.T, PlateA);
        await s.Service.UpdatePricingRuleAsync(s.T.Lot.Id, s.Rule.Id, Update(s.Rule, NewPrice));

        var result = await s.Service.UpdatePricingRuleAsync(s.T.Lot.Id, s.Rule.Id, Update(s.Rule, OldPrice));

        result.PriceUpdateStatus.Should().Be("Unchanged");
        result.PendingPrice.Should().BeNull();
        s.T.Db.PendingPriceChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task Other_Fields_Of_The_Rule_Change_At_Once_Even_While_The_Price_Waits()
    {
        var s = await NewSetupAsync();
        await ParkAsync(s.T, PlateA);
        var model = Update(s.Rule, NewPrice);
        model.Name = "Car (renamed)";

        var result = await s.Service.UpdatePricingRuleAsync(s.T.Lot.Id, s.Rule.Id, model);

        result.Name.Should().Be("Car (renamed)");
        result.BlockPrice.Should().Be(OldPrice);
        result.PriceUpdateStatus.Should().Be("Pending");
    }

    [Fact]
    public async Task A_Manager_Cannot_Change_A_Rule_Shared_By_The_Whole_System()
    {
        var s = await NewSetupAsync();
        s.Rule.ParkingLotId = null;
        await s.T.Db.SaveChangesAsync();

        var act = () => s.Service.UpdatePricingRuleAsync(s.T.Lot.Id, s.Rule.Id, Update(s.Rule, NewPrice));

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*shared pricing rule*");
        (await LivePriceAsync(s)).Should().Be(OldPrice);
    }

    [Fact]
    public async Task An_Admin_Can_Still_Change_A_Shared_Rule_And_It_Takes_Effect_At_Once()
    {
        var s = await NewSetupAsync();
        s.Rule.ParkingLotId = null;
        await s.T.Db.SaveChangesAsync();
        await ParkAsync(s.T, PlateA);
        s.T.ActAs("admin-1", "Admin");

        var result = await s.Service.UpdatePricingRuleAsync(s.T.Lot.Id, s.Rule.Id, Update(s.Rule, NewPrice));

        result.PriceUpdateStatus.Should().Be("Applied");
        (await LivePriceAsync(s)).Should().Be(NewPrice);
    }

    // ---- vehicles in and out ---------------------------------------------------------------------

    [Fact]
    public async Task Entry_Counts_The_Vehicle_In_And_A_Turned_Back_Vehicle_Is_Counted_Out()
    {
        var s = await NewSetupAsync();
        s.T.ActAs(s.T.StaffUserId, "Staff");

        var entry = await s.T.Gates.CheckInAsync(s.T.Lot.Id, new CheckInModel { PlateNumber = PlateA, VehicleTypeId = s.T.TypeId("CAR_4") });
        (await s.LotAsync()).OccupiedBays.Should().Be(1, "the session is open as soon as the barrier lifts");

        await s.T.Gates.CancelEntryAsync(entry.Session.Id);
        (await s.LotAsync()).OccupiedBays.Should().Be(0);
    }

    [Fact]
    public async Task The_Last_Vehicle_To_Leave_Makes_The_Waiting_Price_Live()
    {
        var s = await NewSetupAsync();
        var first = await ParkAsync(s.T, PlateA);
        var second = await ParkAsync(s.T, PlateB);
        await s.Service.UpdatePricingRuleAsync(s.T.Lot.Id, s.Rule.Id, Update(s.Rule, NewPrice));

        await LeaveAsync(s.T, first, PlateA);
        (await LivePriceAsync(s)).Should().Be(OldPrice, "one vehicle is still inside");
        s.T.Db.PendingPriceChanges.Should().ContainSingle();

        await LeaveAsync(s.T, second, PlateB);

        (await LivePriceAsync(s)).Should().Be(NewPrice);
        s.T.Db.PendingPriceChanges.Should().BeEmpty();
        (await s.LotAsync()).OccupiedBays.Should().Be(0);
        var rule = await s.T.Db.PricingRules.AsNoTracking().SingleAsync(r => r.Id == s.Rule.Id);
        rule.VersionNo.Should().Be(2);
        s.T.Db.PricingRuleHistories.Should().Contain(h => h.PricingRuleId == s.Rule.Id && h.ChangeType == "PriceApplied" && h.ChangedBy == s.T.ManagerUserId);
    }

    [Fact]
    public async Task A_Manager_Close_Of_The_Last_Session_Also_Makes_The_Waiting_Price_Live()
    {
        var s = await NewSetupAsync();
        var id = await ParkAsync(s.T, PlateA);
        await s.Service.UpdatePricingRuleAsync(s.T.Lot.Id, s.Rule.Id, Update(s.Rule, NewPrice));

        await s.T.Gates.CloseAbnormallyAsync(id, "Ghost session");

        (await LivePriceAsync(s)).Should().Be(NewPrice);
        (await s.LotAsync()).OccupiedBays.Should().Be(0);
    }

    // ---- stays and bookings keep their price -----------------------------------------------------

    [Fact]
    public async Task A_Stay_Is_Charged_The_Price_It_Entered_With()
    {
        var s = await NewSetupAsync();
        var id = await ParkAsync(s.T, PlateA);
        TariffSnapshot.Read(s.T.Db.ParkingSessions.Single(x => x.Id == id).PriceSnapshotJson)!.BlockPrice.Should().Be(OldPrice);

        // Whatever changes the rule from now on (here straight in the table) cannot reach the stay in progress
        s.Rule.BlockPrice = 50_000;
        await s.T.Db.SaveChangesAsync();
        s.T.Clock.Advance(TimeSpan.FromMinutes(10));
        s.T.ActAs(s.T.StaffUserId, "Staff");
        var quote = await s.T.Gates.RequestPaymentAsync(id, new CheckoutQuoteRequestModel());

        quote.Fee.Should().Be(OldPrice);
    }

    [Fact]
    public async Task A_Stay_Without_A_Saved_Price_Falls_Back_To_The_Live_Rule()
    {
        var s = await NewSetupAsync();
        var id = await ParkAsync(s.T, PlateA);
        s.T.Db.ParkingSessions.Single(x => x.Id == id).PriceSnapshotJson = null;
        s.Rule.BlockPrice = 50_000;
        await s.T.Db.SaveChangesAsync();
        s.T.Clock.Advance(TimeSpan.FromMinutes(10));
        s.T.ActAs(s.T.StaffUserId, "Staff");

        var quote = await s.T.Gates.RequestPaymentAsync(id, new CheckoutQuoteRequestModel());

        quote.Fee.Should().Be(50_000);
    }

    [Fact]
    public async Task A_Booking_Locks_The_Price_And_The_Check_In_Keeps_It()
    {
        var s = await NewSetupAsync();
        var (reservation, _) = await s.T.ConfirmedReservationAsync(arrivalInMinutes: 5);
        TariffSnapshot.Read(s.T.Db.Reservations.Single(r => r.Id == reservation.Id).PriceSnapshotJson)!.BlockPrice.Should().Be(OldPrice);

        // The lot's price changes before the booked car arrives
        s.Rule.BlockPrice = 50_000;
        await s.T.Db.SaveChangesAsync();
        s.T.ActAs(s.T.StaffUserId, "Staff");
        var booked = await s.T.Gates.CheckInAsync(s.T.Lot.Id, new CheckInModel { PlateNumber = s.T.Car.PlateNumber, ReservationCode = reservation.Code });
        var walkIn = await s.T.Gates.CheckInAsync(s.T.Lot.Id, new CheckInModel { PlateNumber = PlateB, VehicleTypeId = s.T.TypeId("CAR_4") });

        TariffSnapshot.Read(s.T.Db.ParkingSessions.Single(x => x.Id == booked.Session.Id).PriceSnapshotJson)!.BlockPrice.Should().Be(OldPrice);
        TariffSnapshot.Read(s.T.Db.ParkingSessions.Single(x => x.Id == walkIn.Session.Id).PriceSnapshotJson)!.BlockPrice.Should().Be(50_000);
    }

    // ---- safety net and the retry loop -----------------------------------------------------------

    [Fact]
    public async Task The_Idle_Lot_Job_Settles_A_Waiting_Price_When_No_Vehicle_Is_Really_Inside()
    {
        var s = await NewSetupAsync();
        var id = await ParkAsync(s.T, PlateA);
        await s.Service.UpdatePricingRuleAsync(s.T.Lot.Id, s.Rule.Id, Update(s.Rule, NewPrice));

        // A vehicle is inside: nothing to settle
        (await s.T.Occupancy.SettleIdleLotsAsync()).Should().Be(0);
        (await LivePriceAsync(s)).Should().Be(OldPrice);

        // The session ended but the exit could not count it out (counter still 1)
        s.T.Db.ParkingSessions.Single(x => x.Id == id).Status = SessionStatus.Completed;
        await s.T.Db.SaveChangesAsync();
        (await s.LotAsync()).OccupiedBays.Should().Be(1);

        (await s.T.Occupancy.SettleIdleLotsAsync()).Should().Be(1);

        (await LivePriceAsync(s)).Should().Be(NewPrice);
        (await s.LotAsync()).OccupiedBays.Should().Be(0);
    }

    [Fact]
    public async Task A_Conflicting_Save_Is_Retried_From_A_Clean_State()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var attempts = 0;

        var result = await LotConcurrency.RunAsync(t.Db, () =>
        {
            attempts++;
            t.Db.PendingPriceChanges.Add(new PendingPriceChange { ParkingLotId = t.Lot.Id, PricingRuleId = 1, ProposedValuesJson = "{}" });
            if (attempts < 3)
                throw new DbUpdateConcurrencyException("lot changed");
            return Task.FromResult(attempts);
        });

        result.Should().Be(3);
        t.Db.ChangeTracker.Entries<PendingPriceChange>().Should().ContainSingle("the rows staged by the failed attempts were dropped");
    }

    [Fact]
    public async Task A_Lot_That_Keeps_Changing_Gives_A_Busy_Error_Instead_Of_Looping_Forever()
    {
        var t = await new OperationsTestContext().SeedAsync();

        var act = () => LotConcurrency.RunAsync<int>(t.Db, () => throw new DbUpdateConcurrencyException("lot changed"));

        await act.Should().ThrowAsync<ConflictException>().WithMessage("*busy*");
    }
}
