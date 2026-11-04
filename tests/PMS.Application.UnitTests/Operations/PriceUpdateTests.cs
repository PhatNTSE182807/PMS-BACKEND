using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.Application.Jobs;
using PMS.Application.Models.Notifications;
using PMS.Application.Models.Operations;
using PMS.Application.Models.Pricing;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>
/// A Manager's new price is live at once and every customer is told. Vehicles already inside keep the price they
/// entered with; every vehicle entering afterwards pays the new one, booked or not. (Real races need SQL Server's
/// rowversion and are tested by hand; here only the retry loop around them is exercised.)
/// </summary>
public class PriceUpdateTests
{
    private const decimal OldPrice = 20_000;
    private const decimal NewPrice = 30_000;
    private const string PlateA = "51A-123.45";
    private const string PlateB = "51B-222.33";
    private const string EventKey = "rule-1-20261104";

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
        var service = new PricingRuleService(t.Db, new LotScopeService(t.Db, t.Claims), new TariffProvider(t.Db, t.Options), t.Options, t.Clock, notifier);
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

    private static decimal SnapshotPrice(OperationsTestContext t, long sessionId) =>
        TariffSnapshot.Read(t.Db.ParkingSessions.Single(x => x.Id == sessionId).PriceSnapshotJson)!.BlockPrice;

    private static async Task<decimal> LivePriceAsync(Setup s) =>
        (await s.T.Db.PricingRules.AsNoTracking().SingleAsync(r => r.Id == s.Rule.Id)).BlockPrice!.Value;

    // ---- Manager updates the price ----------------------------------------------------------------

    [Fact]
    public async Task A_New_Price_Is_Live_At_Once_And_Customers_Are_Told()
    {
        var s = await NewSetupAsync();

        var result = await s.Service.UpdatePricingRuleAsync(s.T.Lot.Id, s.Rule.Id, Update(s.Rule, NewPrice));

        result.PriceUpdateStatus.Should().Be("Applied");
        result.BlockPrice.Should().Be(NewPrice);
        result.VersionNo.Should().Be(2);
        (await LivePriceAsync(s)).Should().Be(NewPrice);
        await s.Notifier.Received(1).PriceChangedAsync(Arg.Is<LotPriceNotice>(n =>
            n.Message.Contains("from 20,000 VND per hour to 30,000 VND per hour") && n.Message.Contains("already parked keep the price")));
    }

    [Fact]
    public async Task A_New_Price_Is_Live_At_Once_With_Vehicles_Inside()
    {
        var s = await NewSetupAsync();
        await ParkAsync(s.T, PlateA);

        var result = await s.Service.UpdatePricingRuleAsync(s.T.Lot.Id, s.Rule.Id, Update(s.Rule, NewPrice));

        result.PriceUpdateStatus.Should().Be("Applied");
        (await LivePriceAsync(s)).Should().Be(NewPrice);
        (await s.LotAsync()).OccupiedBays.Should().Be(1);
    }

    [Fact]
    public async Task Saving_The_Same_Price_Tells_Nobody()
    {
        var s = await NewSetupAsync();

        var result = await s.Service.UpdatePricingRuleAsync(s.T.Lot.Id, s.Rule.Id, Update(s.Rule, OldPrice));

        result.PriceUpdateStatus.Should().Be("Unchanged");
        await s.Notifier.DidNotReceive().PriceChangedAsync(Arg.Any<LotPriceNotice>());
    }

    [Fact]
    public async Task A_Rule_That_Charges_Nobody_Tells_Nobody()
    {
        var s = await NewSetupAsync();
        s.Rule.Status = "Draft";
        await s.T.Db.SaveChangesAsync();
        var model = Update(s.Rule, NewPrice);
        model.Status = "Draft";

        var result = await s.Service.UpdatePricingRuleAsync(s.T.Lot.Id, s.Rule.Id, model);

        result.BlockPrice.Should().Be(NewPrice);
        await s.Notifier.DidNotReceive().PriceChangedAsync(Arg.Any<LotPriceNotice>());
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
    public async Task An_Admin_Can_Still_Change_A_Shared_Rule()
    {
        var s = await NewSetupAsync();
        s.Rule.ParkingLotId = null;
        await s.T.Db.SaveChangesAsync();
        s.T.ActAs("admin-1", "Admin");

        var result = await s.Service.UpdatePricingRuleAsync(s.T.Lot.Id, s.Rule.Id, Update(s.Rule, NewPrice));

        result.PriceUpdateStatus.Should().Be("Applied");
        (await LivePriceAsync(s)).Should().Be(NewPrice);
    }

    // ---- who pays what ---------------------------------------------------------------------------

    [Fact]
    public async Task A_Vehicle_Inside_Keeps_The_Old_Price_And_A_Walk_In_After_The_Change_Pays_The_New_One()
    {
        var s = await NewSetupAsync();
        var inside = await ParkAsync(s.T, PlateA);

        await s.Service.UpdatePricingRuleAsync(s.T.Lot.Id, s.Rule.Id, Update(s.Rule, NewPrice));
        var next = await ParkAsync(s.T, PlateB);

        s.T.Clock.Advance(TimeSpan.FromMinutes(10));
        s.T.ActAs(s.T.StaffUserId, "Staff");
        (await s.T.Gates.RequestPaymentAsync(inside, new CheckoutQuoteRequestModel())).Fee.Should().Be(OldPrice);
        (await s.T.Gates.RequestPaymentAsync(next, new CheckoutQuoteRequestModel())).Fee.Should().Be(NewPrice);
    }

    [Fact]
    public async Task A_Booking_Made_Before_The_Change_Pays_The_New_Price_When_It_Enters_After_It()
    {
        var s = await NewSetupAsync();
        var (reservation, _) = await s.T.ConfirmedReservationAsync(arrivalInMinutes: 5);

        await s.Service.UpdatePricingRuleAsync(s.T.Lot.Id, s.Rule.Id, Update(s.Rule, NewPrice));
        s.T.ActAs(s.T.StaffUserId, "Staff");
        var booked = await s.T.Gates.CheckInAsync(s.T.Lot.Id, new CheckInModel { PlateNumber = s.T.Car.PlateNumber, ReservationCode = reservation.Code });

        SnapshotPrice(s.T, booked.Session.Id).Should().Be(NewPrice);
    }

    [Fact]
    public async Task A_Booked_Vehicle_Already_Inside_Keeps_The_Old_Price()
    {
        var s = await NewSetupAsync();
        var (reservation, _) = await s.T.ConfirmedReservationAsync(arrivalInMinutes: 5);
        s.T.ActAs(s.T.StaffUserId, "Staff");
        var booked = await s.T.Gates.CheckInAsync(s.T.Lot.Id, new CheckInModel { PlateNumber = s.T.Car.PlateNumber, ReservationCode = reservation.Code });
        s.T.ActAs(s.T.ManagerUserId, "Manager");

        await s.Service.UpdatePricingRuleAsync(s.T.Lot.Id, s.Rule.Id, Update(s.Rule, NewPrice));

        SnapshotPrice(s.T, booked.Session.Id).Should().Be(OldPrice);
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
    public async Task Entry_Counts_The_Vehicle_In_And_A_Turned_Back_Vehicle_Is_Counted_Out()
    {
        var s = await NewSetupAsync();
        s.T.ActAs(s.T.StaffUserId, "Staff");

        var entry = await s.T.Gates.CheckInAsync(s.T.Lot.Id, new CheckInModel { PlateNumber = PlateA, VehicleTypeId = s.T.TypeId("CAR_4") });
        (await s.LotAsync()).OccupiedBays.Should().Be(1, "the session is open as soon as the barrier lifts");

        await s.T.Gates.CancelEntryAsync(entry.Session.Id);
        (await s.LotAsync()).OccupiedBays.Should().Be(0);
    }

    // ---- customers are told ----------------------------------------------------------------------

    private static (NotificationJobs Jobs, IUserNotificationNotifier Push) NewJobs(OperationsTestContext t, params string[] customers)
    {
        var audience = Substitute.For<ICustomerAudience>();
        audience.ActiveCustomerIdsAsync().Returns(customers);
        var push = Substitute.For<IUserNotificationNotifier>();
        return (new NotificationJobs(t.Db, t.Clock, push, audience), push);
    }

    [Fact]
    public async Task Every_Active_Customer_Gets_One_Notification_And_A_Push()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (jobs, push) = NewJobs(t, "c1", "c2");

        var written = await jobs.LotPriceChangedAsync("Price changed at Central", "Changed", EventKey, 7, "Central");

        written.Should().Be(2);
        var saved = t.Db.UserNotifications.AsNoTracking().OrderBy(n => n.UserId).ToList();
        saved.Select(n => n.UserId).Should().Equal("c1", "c2");
        saved.Should().OnlyContain(n => n.Type == NotificationTypes.PriceChanged && n.RefId == EventKey);
        await push.Received(1).NotificationCreatedAsync("c1", Arg.Is<NotificationModel>(n => n.Data["lotName"] == "Central"), 1);
        await push.Received(1).NotificationCreatedAsync("c2", Arg.Any<NotificationModel>(), 1);
    }

    [Fact]
    public async Task A_Retry_Only_Reaches_The_Customers_It_Missed()
    {
        var t = await new OperationsTestContext().SeedAsync();
        await NewJobs(t, "c1").Jobs.LotPriceChangedAsync("T", "M", EventKey, 7, "Central");

        var written = await NewJobs(t, "c1", "c2").Jobs.LotPriceChangedAsync("T", "M", EventKey, 7, "Central");

        written.Should().Be(1);
        t.Db.UserNotifications.Count().Should().Be(2);
    }

    [Fact]
    public async Task One_Failed_Push_Does_Not_Stop_The_Others()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (jobs, push) = NewJobs(t, "c1", "c2");
        push.NotificationCreatedAsync("c1", Arg.Any<NotificationModel>(), Arg.Any<int>()).Returns(Task.FromException(new InvalidOperationException("gone")));

        (await jobs.LotPriceChangedAsync("T", "M", EventKey, 7, "Central")).Should().Be(2);

        await push.Received(1).NotificationCreatedAsync("c2", Arg.Any<NotificationModel>(), 1);
    }

    [Fact]
    public async Task The_Default_Notifier_Fills_The_Bell_Even_When_The_Live_Push_Fails()
    {
        var realtime = Substitute.For<ILotRealtimeNotifier>();
        realtime.LotPriceChangedAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromException(new InvalidOperationException("gone")));
        var publisher = Substitute.For<IUserNotificationPublisher>();
        var notice = new LotPriceNotice(7, "Central", "4-Seat Car", "30,000 VND per 60 min", "20,000 VND per 60 min", EventKey);

        await new LotPriceNotifier(realtime, notifications: publisher).PriceChangedAsync(notice);

        publisher.Received(1).LotPriceChanged("4-Seat Car price changed at Central", notice.Message, EventKey, 7, "Central");
    }

    [Fact]
    public void The_Message_Does_Not_Say_From_X_To_X_When_Only_Another_Fee_Changed()
    {
        var notice = new LotPriceNotice(7, "Central", null, "30,000 VND per 60 min", "30,000 VND per 60 min", EventKey);

        notice.Message.Should().StartWith("Price for Central is now 30,000 VND per 60 min.");
    }

    // ---- the retry loop ---------------------------------------------------------------------------

    [Fact]
    public async Task A_Conflicting_Save_Is_Retried_From_A_Clean_State()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var attempts = 0;

        var result = await LotConcurrency.RunAsync(t.Db, () =>
        {
            attempts++;
            t.Db.PricingRuleHistories.Add(new PricingRuleHistory { PricingRuleId = 1, ChangeType = "Update" });
            if (attempts < 3)
                throw new DbUpdateConcurrencyException("lot changed");
            return Task.FromResult(attempts);
        });

        result.Should().Be(3);
        t.Db.ChangeTracker.Entries<PricingRuleHistory>().Should().ContainSingle("the rows staged by the failed attempts were dropped");
    }

    [Fact]
    public async Task A_Lot_That_Keeps_Changing_Gives_A_Busy_Error_Instead_Of_Looping_Forever()
    {
        var t = await new OperationsTestContext().SeedAsync();

        var act = () => LotConcurrency.RunAsync<int>(t.Db, () => throw new DbUpdateConcurrencyException("lot changed"));

        await act.Should().ThrowAsync<ConflictException>().WithMessage("*busy*");
    }
}
