using System;
using FluentAssertions;
using PMS.Application.Helpers.Parking;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>
/// The fee table of SRS section 5 (car; free when leaving within 15 minutes, otherwise charged from the
/// first minute; 20,000 VND per 1-hour block rounded up; 150,000 VND cap per calendar day; 10,000 VND
/// late charge after the Exit Window). Phase 1 acceptance requires all six examples to be right.
/// </summary>
public class FeeCalculatorTests
{
    private static readonly TimeZoneInfo Vietnam = ParkingText.FindTimeZone("SE Asia Standard Time");

    private static readonly Tariff SrsCar = new()
    {
        GraceMinutes = 15,
        BlockMinutes = 60,
        BlockPrice = 20_000,
        MaxDailyFee = 150_000
    };

    /// <summary>Local Vietnam time (UTC+7) to UTC.</summary>
    private static DateTime Local(int day, int hour, int minute) =>
        new DateTime(2026, 10, day, hour, minute, 0, DateTimeKind.Utc).AddHours(-7);

    private static decimal Fee(DateTime entry, DateTime exit) => FeeCalculator.Calculate(SrsCar, entry, exit, Vietnam).Total;

    [Fact]
    public void Example1_Leaving_Within_The_Free_Period_Costs_Nothing()
    {
        var result = FeeCalculator.Calculate(SrsCar, Local(20, 8, 0), Local(20, 8, 10), Vietnam);

        result.Total.Should().Be(0);
        result.WithinGrace.Should().BeTrue();
    }

    [Fact]
    public void Example2_Two_Hours_Twenty_Is_Three_Blocks()
    {
        var result = FeeCalculator.Calculate(SrsCar, Local(20, 8, 0), Local(20, 10, 20), Vietnam);

        result.Total.Should().Be(60_000);
        result.Days.Should().ContainSingle().Which.Blocks.Should().Be(3);
    }

    [Fact]
    public void Example3_Deposit_Is_Deducted_From_The_Fee()
    {
        var fee = Fee(Local(20, 8, 0), Local(20, 10, 20));

        var settlement = FeeCalculator.Settle(fee, deposit: 30_000, alreadyPaid: 0, exitWindowMissed: false, exitWindowLateFee: 10_000);

        settlement.AmountDue.Should().Be(30_000);
        settlement.DepositApplied.Should().Be(30_000);
        settlement.DepositRefund.Should().Be(0);
    }

    [Fact]
    public void Example4_Deposit_Above_The_Fee_Refunds_The_Rest()
    {
        var fee = Fee(Local(20, 8, 0), Local(20, 8, 40));

        var settlement = FeeCalculator.Settle(fee, deposit: 30_000, alreadyPaid: 0, exitWindowMissed: false, exitWindowLateFee: 10_000);

        fee.Should().Be(20_000);
        settlement.AmountDue.Should().Be(0);
        settlement.DepositRefund.Should().Be(10_000);
    }

    [Fact]
    public void Example5_Missing_The_Exit_Window_Recalculates_And_Adds_The_Late_Charge()
    {
        var prepaid = Fee(Local(20, 8, 0), Local(20, 10, 0));
        var feeAtExit = Fee(Local(20, 8, 0), Local(20, 10, 20));

        var settlement = FeeCalculator.Settle(feeAtExit, deposit: 0, alreadyPaid: prepaid, exitWindowMissed: true, exitWindowLateFee: 10_000);

        prepaid.Should().Be(40_000);
        feeAtExit.Should().Be(60_000);
        settlement.AmountDue.Should().Be(30_000);
    }

    [Fact]
    public void Example6_Overnight_Caps_Each_Calendar_Day()
    {
        var result = FeeCalculator.Calculate(SrsCar, Local(19, 20, 0), Local(20, 10, 0), Vietnam);

        result.Total.Should().Be(230_000);
        result.Days.Should().HaveCount(2);
        result.Days[0].Should().BeEquivalentTo(new { Blocks = 4, Amount = 80_000m, Capped = false });
        result.Days[1].Should().BeEquivalentTo(new { Blocks = 10, Amount = 150_000m, Capped = true });
    }

    [Fact]
    public void Exactly_The_Free_Period_Is_Still_Free_And_One_Minute_More_Pays_From_The_Start()
    {
        Fee(Local(20, 8, 0), Local(20, 8, 15)).Should().Be(0);
        Fee(Local(20, 8, 0), Local(20, 8, 16)).Should().Be(20_000);
    }

    [Fact]
    public void Fees_Are_Rounded_Up_To_A_Thousand()
    {
        var tariff = SrsCar with { BlockPrice = 12_345 };

        FeeCalculator.Calculate(tariff, Local(20, 8, 0), Local(20, 9, 0), Vietnam).Total.Should().Be(13_000);
    }

    [Fact]
    public void Day_And_Night_Blocks_Use_Their_Own_Price()
    {
        var tariff = new Tariff
        {
            ChargeModel = "DayNight",
            BlockMinutes = 60,
            BlockPrice = 1,
            DayStart = new TimeOnly(6, 0),
            NightStart = new TimeOnly(18, 0),
            DayPrice = 10_000,
            NightPrice = 30_000
        };

        // 17:00-19:00 local: one day block + one night block
        FeeCalculator.Calculate(tariff, Local(20, 17, 0), Local(20, 19, 0), Vietnam).Total.Should().Be(40_000);
    }

    [Fact]
    public void Per_Entry_Tariff_Charges_Once_Per_Calendar_Day()
    {
        var tariff = new Tariff { ChargeModel = "PerEntry", EntryFee = 25_000 };

        FeeCalculator.Calculate(tariff, Local(19, 20, 0), Local(20, 10, 0), Vietnam).Total.Should().Be(50_000);
    }

    [Fact]
    public void Lost_Ticket_Fine_Is_Added_And_Paid_Money_Is_Never_Refunded_At_Exit()
    {
        var settlement = FeeCalculator.Settle(fee: 20_000, deposit: 0, alreadyPaid: 40_000, exitWindowMissed: false, exitWindowLateFee: 10_000, lostTicketFee: 50_000);

        settlement.AmountDue.Should().Be(30_000);
        settlement.DepositRefund.Should().Be(0);
    }
}
