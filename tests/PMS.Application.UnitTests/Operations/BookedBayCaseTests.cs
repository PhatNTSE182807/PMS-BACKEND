using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using PMS.Application.Exceptions;
using PMS.Application.Models.Notifications;
using PMS.Application.Models.Operations;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>A walk-in parks in a booked bay (BookedBayCaseService, BR-08 with the team's compensation rule).</summary>
public class BookedBayCaseTests
{
    private const decimal Compensation = 50_000m;

    /// <summary>A walk-in enters, then is recorded in the booking's bay, as the Admin simulation does.</summary>
    private static async Task<(BookedBayCase Case, Reservation Booking, ParkingSession WalkIn)> WalkInTakesBookedBayAsync(OperationsTestContext t)
    {
        var (booking, _) = await t.ConfirmedReservationAsync(arrivalInMinutes: 30);
        var entry = await t.Gates.CheckInAsync(t.Lot.Id, new CheckInModel
        {
            PlateNumber = "51G-111.22", VehicleTypeId = t.TypeId("CAR_4"), PlateConfirmed = true, IgnoreReservation = true
        });
        entry.Outcome.Should().Be("Created");
        await t.Gates.ConfirmEntryAsync(entry.Session.Id);

        var reservation = t.Db.Reservations.Single(r => r.Id == booking.Id);
        var bay = t.Db.Slots.Single(s => s.Id == reservation.SlotId);
        var walkIn = t.Db.ParkingSessions.Single(s => s.Id == entry.Session.Id);
        walkIn.SlotId = bay.Id;
        walkIn.ZoneId = bay.ZoneId;
        await t.Db.SaveChangesAsync();

        var bayCase = await t.BookedBays.OpenIfBookedAsync(walkIn.Id);
        return (bayCase, reservation, walkIn);
    }

    /// <summary>The lot's 4-seat rule pays <see cref="Compensation"/>.</summary>
    private static async Task AddCompensationRuleAsync(OperationsTestContext t)
    {
        t.Db.PricingRules.Add(new PricingRule
        {
            Code = "PR-CAR4", Name = "Cars", ParkingLotId = t.Lot.Id, VehicleTypeId = t.TypeId("CAR_4"), ChargeModel = "Block",
            BlockMinutes = 60, BlockPrice = 20_000, Currency = "VND", Priority = 100, Status = "Active", VersionNo = 1,
            EffectiveFrom = t.Now.AddDays(-1), CreatedBy = "test", CreatedAt = t.Now, BookedBayCompensation = Compensation
        });
        await t.Db.SaveChangesAsync();
    }

    /// <summary>Every other car bay goes under maintenance, so no bay is left to move the booking to.</summary>
    private static async Task FillOtherCarBaysAsync(OperationsTestContext t, int keepBayId)
    {
        var cars = new[] { t.TypeId("CAR_4"), t.TypeId("CAR_7") };
        foreach (var slot in t.Db.Slots.Where(s => cars.Contains(s.VehicleTypeId) && s.Id != keepBayId && s.Status == SlotStatus.Available))
            slot.Status = SlotStatus.Maintenance;
        await t.Db.SaveChangesAsync();
    }

    private static decimal WalletBalance(OperationsTestContext t) =>
        t.Db.Wallets.Where(w => w.CustomerId == t.Customer.Id).Select(w => w.Balance).FirstOrDefault();

    [Fact]
    public async Task A_Walk_In_In_A_Booked_Bay_Opens_One_Case_And_Tells_The_Customer()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (bayCase, booking, walkIn) = await WalkInTakesBookedBayAsync(t);

        bayCase.Should().NotBeNull();
        bayCase.Status.Should().Be(BookedBayCaseStatus.Open);
        bayCase.ReservationId.Should().Be(booking.Id);
        t.Db.Slots.Single(s => s.Id == booking.SlotId).Status.Should().Be(SlotStatus.Occupied);
        t.Publisher.Received(1).BookedBayCaseChanged(bayCase.Id, NotificationTypes.BookedBayTaken);

        (await t.BookedBays.OpenIfBookedAsync(walkIn.Id)).Should().BeNull("one case per booking");
        var listed = await t.BookedBays.GetForLotAsync(t.Lot.Id, openOnly: true);
        listed.Should().ContainSingle();
        listed[0].WalkInPlate.Should().Be("51G-111.22");
        listed[0].FreeBays.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_Customer_Notification_Names_The_Bay_But_Never_The_Walk_Ins_Plate()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (bayCase, booking, _) = await WalkInTakesBookedBayAsync(t);
        var jobs = new PMS.Application.Jobs.NotificationJobs(t.Db, t.Clock);

        (await jobs.BookedBayCaseAsync(bayCase.Id, NotificationTypes.BookedBayTaken)).Should().BeTrue();
        (await jobs.BookedBayCaseAsync(bayCase.Id, NotificationTypes.BookedBayTaken)).Should().BeFalse("a retried job never notifies twice");

        var sent = t.Db.UserNotifications.Single(n => n.UserId == t.CustomerUserId && n.Type == NotificationTypes.BookedBayTaken);
        var bayCode = t.Db.Slots.Single(s => s.Id == booking.SlotId).Code;
        sent.Message.Should().Contain(bayCode).And.NotContain("51G-111.22");
    }

    [Fact]
    public async Task Staff_Move_The_Booking_And_It_Checks_In_With_The_First_Hour_Free()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (bayCase, booking, _) = await WalkInTakesBookedBayAsync(t);
        var listed = (await t.BookedBays.GetForLotAsync(t.Lot.Id, openOnly: true)).Single();
        var target = listed.FreeBays[0];
        var zone = t.Db.Slots.Single(s => s.Id == target.SlotId).ZoneId;
        var freeBefore = t.CounterOf(zone).AvailableSpaces;

        var moved = await t.BookedBays.ReassignAsync(bayCase.Id, new ReassignBookedBayModel { SlotId = target.SlotId });

        moved.Status.Should().Be(BookedBayCaseStatus.Reassigned);
        moved.NewBayCode.Should().Be(target.Code);
        t.Db.Reservations.Single(r => r.Id == booking.Id).SlotId.Should().Be(target.SlotId);
        t.Db.Slots.Single(s => s.Id == target.SlotId).Status.Should().Be(SlotStatus.Reserved);
        t.CounterOf(zone).AvailableSpaces.Should().BeLessThanOrEqualTo(freeBefore, "the hold moved, it was not counted twice");
        t.Publisher.Received(1).BookedBayCaseChanged(bayCase.Id, NotificationTypes.BookedBayReassigned);

        // The customer arrives on time (within the arrival window)
        t.Clock.Advance(booking.StartTime - t.Now);
        var entry = await t.Gates.CheckInAsync(t.Lot.Id, new CheckInModel { PlateNumber = t.Car.PlateNumber, ReservationCode = booking.ReservationCode });
        entry.Outcome.Should().Be("Created", string.Join("; ", entry.Alerts.Select(a => a.Message)));
        t.Db.ParkingSessions.Single(s => s.Id == entry.Session.Id).FirstHourWaived.Should().BeTrue();
    }

    [Fact]
    public async Task Cancelling_Is_Refused_While_Another_Bay_Is_Free()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (bayCase, _, _) = await WalkInTakesBookedBayAsync(t);

        await t.BookedBays.Invoking(b => b.CompensateAsync(bayCase.Id, new CompensateBookedBayModel()))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*move the booking there instead*");
    }

    [Fact]
    public async Task With_No_Bay_Left_The_Booking_Is_Cancelled_And_Deposit_Plus_Compensation_Go_To_The_Wallet()
    {
        var t = await new OperationsTestContext().SeedAsync();
        await AddCompensationRuleAsync(t);
        var (bayCase, booking, _) = await WalkInTakesBookedBayAsync(t);
        await FillOtherCarBaysAsync(t, booking.SlotId!.Value);
        var deposit = booking.DepositAmount;
        var walletBefore = WalletBalance(t);
        var zone = t.Db.Slots.Single(s => s.Id == booking.SlotId).ZoneId;
        var freeBefore = t.CounterOf(zone).AvailableSpaces;

        var result = await t.BookedBays.CompensateAsync(bayCase.Id, new CompensateBookedBayModel { Note = "Lot full" });

        result.Status.Should().Be(BookedBayCaseStatus.Compensated);
        result.DepositRefunded.Should().Be(deposit);
        result.CompensationPaid.Should().Be(Compensation);
        t.Db.Reservations.Single(r => r.Id == booking.Id).Status.Should().Be(ReservationStatus.LotCancelled);
        WalletBalance(t).Should().Be(walletBefore + deposit + Compensation);
        t.Db.WalletTransactions.Should().Contain(w => w.TxnType == "Refund" && w.Amount == deposit);
        t.Db.WalletTransactions.Should().Contain(w => w.TxnType == "Compensation" && w.Amount == Compensation);
        t.Db.Payments.Should().ContainSingle(p => p.Purpose == PaymentPurpose.Compensation && p.Status == PaymentStatus.Succeeded
                                                  && p.ParkingLotId == t.Lot.Id && p.Amount == Compensation);
        t.CounterOf(zone).AvailableSpaces.Should().Be(freeBefore + 1, "the booking's held space goes back to the zone");
        t.Publisher.Received(1).BookedBayCaseChanged(bayCase.Id, NotificationTypes.BookingCancelledByLot);
        t.Publisher.Received(1).BookedBayCaseChanged(bayCase.Id, NotificationTypes.DepositRefunded);
        t.Publisher.Received(1).BookedBayCaseChanged(bayCase.Id, NotificationTypes.CompensationPaid);

        // The compensation comes off the lot's revenue
        var revenue = await new RevenueReportService(t.Db).GetManagerRevenueAsync(t.ManagerUserId,
            t.Now.AddDays(-1).ToString("yyyy-MM-dd"), t.Now.AddDays(1).ToString("yyyy-MM-dd"), t.Lot.Id);
        revenue.TotalCompensation.Should().Be(Compensation);
        revenue.ByLot.Single().Compensation.Should().Be(Compensation);
        revenue.TotalRevenue.Should().Be(-Compensation, "no parking fee was settled in the period");
    }

    [Fact]
    public async Task When_The_Walk_In_Leaves_First_The_Booking_Gets_Its_Bay_Back()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (bayCase, booking, walkIn) = await WalkInTakesBookedBayAsync(t);

        walkIn.Status = SessionStatus.Completed;
        walkIn.ExitTime = t.Now;
        await t.Inventory.LeaveAsync(walkIn.ZoneId, walkIn.SlotId, t.Lot.Id);
        await t.Db.SaveChangesAsync();

        (await t.BookedBays.SweepAsync(t.Lot.Id)).Should().Be(1);

        t.Db.BookedBayCases.Single(c => c.Id == bayCase.Id).Status.Should().Be(BookedBayCaseStatus.Closed);
        t.Db.Slots.Single(s => s.Id == booking.SlotId).Status.Should().Be(SlotStatus.Reserved);
        t.Publisher.Received(1).BookedBayCaseChanged(bayCase.Id, NotificationTypes.BookedBayFreed);
    }

    [Fact]
    public async Task A_Case_Still_Open_When_The_Hold_Ends_Is_Compensated_Never_A_No_Show()
    {
        var t = await new OperationsTestContext().SeedAsync();
        await AddCompensationRuleAsync(t);
        var (bayCase, booking, _) = await WalkInTakesBookedBayAsync(t);
        t.Clock.Advance(booking.EndTime - t.Now + TimeSpan.FromMinutes(1));

        await t.Jobs.RunMinutelyAsync();

        t.Db.Reservations.Single(r => r.Id == booking.Id).Status.Should().Be(ReservationStatus.LotCancelled);
        var closed = t.Db.BookedBayCases.Single(c => c.Id == bayCase.Id);
        closed.Status.Should().Be(BookedBayCaseStatus.Compensated);
        closed.ResolvedBy.Should().Be(BookedBayCaseService.SystemActor);
        closed.CompensationPaid.Should().Be(Compensation);
    }

    [Fact]
    public async Task Staff_Of_Another_Lot_Cannot_See_Or_Resolve_Cases()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var (bayCase, _, _) = await WalkInTakesBookedBayAsync(t);
        t.ActAs("other-staff", "Staff");

        await t.BookedBays.Invoking(b => b.GetForLotAsync(t.Lot.Id, openOnly: true)).Should().ThrowAsync<ForbiddenException>();
        await t.BookedBays.Invoking(b => b.CompensateAsync(bayCase.Id, new CompensateBookedBayModel())).Should().ThrowAsync<ForbiddenException>();
    }
}
