using System;
using System.Threading.Tasks;
using FluentAssertions;
using PMS.Application.Exceptions;
using PMS.Application.Models.Operations;
using PMS.Application.Services;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>
/// Bookings with or without a deposit (30 or 15 minutes of hold after arrival) and the daily limit
/// on online wallet top-ups.
/// </summary>
public class DepositOptionAndWalletLimitTests
{
    private static readonly PaymentRequestContext Context = new("127.0.0.1", "http://api");

    private static CreateReservationModel Booking(OperationsTestContext t, bool payDeposit, int arrivalInMinutes = 60) => new()
    {
        ParkingLotId = t.Lot.Id,
        SlotId = t.FreeCarSlot().Id,
        VehicleId = t.Car.Id,
        ArrivalTime = t.Clock.GetUtcNow().AddMinutes(arrivalInMinutes),
        PayDeposit = payDeposit
    };

    private static TopUpWalletModel TopUp(decimal amount) => new() { Amount = amount, Provider = "VNPay" };

    /* ------------------------------------------------------------------ deposit option */

    [Fact]
    public async Task A_Booking_With_A_Deposit_Waits_For_Payment_And_Holds_The_Slot_30_Minutes()
    {
        var t = await new OperationsTestContext().SeedAsync();

        var booked = await t.Reservations.CreateAsync(t.CustomerUserId, Booking(t, payDeposit: true));

        booked.Status.Should().Be(ReservationStatus.PendingDeposit);
        booked.HasDeposit.Should().BeTrue();
        booked.DepositAmount.Should().Be(30_000);
        booked.HoldMinutes.Should().Be(30);
        booked.CheckInUntil.Should().Be(booked.ArrivalTime.AddMinutes(30));
    }

    [Fact]
    public async Task A_Booking_Without_A_Deposit_Is_Confirmed_At_Once_And_Holds_The_Slot_15_Minutes()
    {
        var t = await new OperationsTestContext().SeedAsync();

        var booked = await t.Reservations.CreateAsync(t.CustomerUserId, Booking(t, payDeposit: false));

        booked.Status.Should().Be(ReservationStatus.Confirmed);
        booked.HasDeposit.Should().BeFalse();
        booked.DepositAmount.Should().Be(0);
        booked.DepositDueAt.Should().BeNull();
        booked.HoldMinutes.Should().Be(15);
        booked.CheckInUntil.Should().Be(booked.ArrivalTime.AddMinutes(15));
        // Confirmed, so the QR is issued right away
        (await t.Reservations.GetQrAsync(t.CustomerUserId, booked.Id)).ValidUntil.Should().Be(booked.CheckInUntil);
    }

    [Fact]
    public async Task A_Booking_Without_A_Deposit_Has_Nothing_To_Pay()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var booked = await t.Reservations.CreateAsync(t.CustomerUserId, Booking(t, payDeposit: false));

        var pay = () => t.Payments.CreateDepositPaymentAsync(booked.Id, t.CustomerUserId, new CreatePaymentModel { Provider = "VNPay" }, Context);

        await pay.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Changing_The_Arrival_Keeps_The_Hold_Length_Of_The_Booking_Type()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var booked = await t.Reservations.CreateAsync(t.CustomerUserId, Booking(t, payDeposit: false, arrivalInMinutes: 120));

        var changed = await t.Reservations.UpdateAsync(t.CustomerUserId, booked.Id,
            new UpdateReservationModel { ArrivalTime = t.Clock.GetUtcNow().AddMinutes(180) });

        changed.HoldMinutes.Should().Be(15);
        changed.CheckInUntil.Should().Be(changed.ArrivalTime.AddMinutes(15));
    }

    /* ------------------------------------------------------------------ wallet daily limit */

    [Fact]
    public async Task Top_Ups_Up_To_The_Daily_Limit_Are_Accepted_And_The_Rest_Is_Refused()
    {
        var t = await new OperationsTestContext().SeedAsync();

        await t.Payments.CreateWalletTopUpPaymentAsync(t.CustomerUserId, TopUp(6_000_000), Context);
        await t.Payments.CreateWalletTopUpPaymentAsync(t.CustomerUserId, TopUp(4_000_000), Context);
        var overLimit = () => t.Payments.CreateWalletTopUpPaymentAsync(t.CustomerUserId, TopUp(10_000), Context);

        await overLimit.Should().ThrowAsync<BadRequestException>().WithMessage("*limit of 10,000,000 VND*");
        (await t.Payments.GetWalletAsync(t.CustomerUserId)).TopUpRemainingToday.Should().Be(0);
    }

    [Fact]
    public async Task A_Top_Up_Over_What_Is_Left_Today_Says_How_Much_Is_Left()
    {
        var t = await new OperationsTestContext().SeedAsync();
        await t.Payments.CreateWalletTopUpPaymentAsync(t.CustomerUserId, TopUp(9_000_000), Context);

        var act = () => t.Payments.CreateWalletTopUpPaymentAsync(t.CustomerUserId, TopUp(2_000_000), Context);

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*still add 1,000,000 VND today*");
    }

    [Fact]
    public async Task An_Expired_Payment_Link_No_Longer_Counts_Against_The_Limit()
    {
        var t = await new OperationsTestContext().SeedAsync();
        await t.Payments.CreateWalletTopUpPaymentAsync(t.CustomerUserId, TopUp(10_000_000), Context);

        // The payment link was never paid and has expired
        t.Clock.Advance(TimeSpan.FromMinutes(t.Options.Gate.PaymentLinkMinutes + 1));

        (await t.Payments.GetWalletAsync(t.CustomerUserId)).TopUpRemainingToday.Should().Be(10_000_000);
        await t.Payments.CreateWalletTopUpPaymentAsync(t.CustomerUserId, TopUp(10_000_000), Context);
    }

    [Fact]
    public async Task The_Limit_Starts_Again_On_The_Next_Calendar_Day()
    {
        var t = await new OperationsTestContext().SeedAsync();
        await t.Payments.CreateWalletTopUpPaymentAsync(t.CustomerUserId, TopUp(10_000_000), Context);

        t.Clock.Advance(TimeSpan.FromDays(1));

        (await t.Payments.GetWalletAsync(t.CustomerUserId)).TopUpRemainingToday.Should().Be(10_000_000);
    }
}
