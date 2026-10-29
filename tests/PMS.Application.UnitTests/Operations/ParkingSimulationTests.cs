using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>The Admin's parking simulation: fees and booking deadlines in seconds, in every lot.</summary>
public class ParkingSimulationTests
{
    private static readonly TimeZoneInfo Vietnam = ParkingText.FindTimeZone("SE Asia Standard Time");
    private static readonly DateTime Entry = new(2026, 10, 20, 1, 0, 0, DateTimeKind.Utc);

    private static UpdateParkingSimulationModel On(int blockSeconds = 5, int holdSeconds = 15) => new()
    {
        Enabled = true,
        BlockSeconds = blockSeconds,
        DepositHoldSeconds = 20,
        HoldSeconds = holdSeconds,
        DepositPaymentSeconds = 10,
        EarlyArrivalSeconds = 7,
        CancelRefundSeconds = 3,
        ExitWindowSeconds = 4
    };

    private static UpdateParkingSimulationModel Off()
    {
        var model = On();
        model.Enabled = false;
        return model;
    }

    private static ParkingSimulationService Service(OperationsTestContext t) => new(t.Db, t.Options, t.Clock);

    // ---- options ----------------------------------------------------------------------------------

    [Fact]
    public void Off_The_Normal_Minutes_Apply()
    {
        var options = new ParkingOptions();

        options.IsSimulating.Should().BeFalse();
        options.HoldTime(true).Should().Be(TimeSpan.FromMinutes(30));
        options.HoldTime(false).Should().Be(TimeSpan.FromMinutes(15));
        options.DepositPaymentTime.Should().Be(TimeSpan.FromMinutes(10));
        options.EarlyArrivalTime.Should().Be(TimeSpan.FromMinutes(15));
        options.CancelRefundTime.Should().Be(TimeSpan.FromMinutes(30));
        options.ExitWindowTime.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Missing_Settings_Count_As_Off()
    {
        var options = new ParkingOptions { Simulation = null };

        options.IsSimulating.Should().BeFalse();
        options.HoldTime(true).Should().Be(TimeSpan.FromMinutes(30));
        options.ExitWindowTime.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void On_Every_Booking_Time_Is_In_Seconds()
    {
        var options = new ParkingOptions
        {
            Simulation = new ParkingSimulationSettings
            {
                Enabled = true, DepositHoldSeconds = 20, HoldSeconds = 15, DepositPaymentSeconds = 10,
                EarlyArrivalSeconds = 7, CancelRefundSeconds = 3, ExitWindowSeconds = 4
            }
        };

        options.IsSimulating.Should().BeTrue();
        options.HoldTime(true).Should().Be(TimeSpan.FromSeconds(20));
        options.HoldTime(false).Should().Be(TimeSpan.FromSeconds(15));
        options.DepositPaymentTime.Should().Be(TimeSpan.FromSeconds(10));
        options.EarlyArrivalTime.Should().Be(TimeSpan.FromSeconds(7));
        options.CancelRefundTime.Should().Be(TimeSpan.FromSeconds(3));
        options.ExitWindowTime.Should().Be(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void A_Copy_Is_A_Separate_Object()
    {
        var settings = new ParkingSimulationSettings { Enabled = true, BlockSeconds = 7 };

        var copy = settings.Copy();

        copy.Should().NotBeSameAs(settings);
        copy.Should().BeEquivalentTo(settings);
    }

    [Theory]
    [InlineData(1, "1 second")]
    [InlineData(15, "15 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(900, "15 minutes")]
    public void Durations_Read_In_Seconds_Or_Minutes(int seconds, string text) =>
        ParkingText.Duration(TimeSpan.FromSeconds(seconds)).Should().Be(text);

    // ---- fee --------------------------------------------------------------------------------------

    private static readonly Tariff Hourly = new() { BlockMinutes = 60, BlockPrice = 30_000, MaxDailyFee = 150_000 };

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(12, 3)]
    public void Each_Block_Lasts_The_Simulated_Seconds(int stayedSeconds, int blocks)
    {
        var fee = FeeCalculator.CalculateSimulated(Hourly, Entry, Entry.AddSeconds(stayedSeconds), Vietnam, 5);

        fee.Days.Single().Blocks.Should().Be(blocks);
        fee.Total.Should().Be(blocks * 30_000);
    }

    [Fact]
    public void The_Daily_Cap_Does_Not_Apply()
    {
        // 60 blocks of 30,000 VND: far above the 150,000 VND cap
        var fee = FeeCalculator.CalculateSimulated(Hourly, Entry, Entry.AddMinutes(5), Vietnam, 5);

        fee.Total.Should().Be(1_800_000);
        fee.Days.Single().Capped.Should().BeFalse();
        fee.DurationMinutes.Should().Be(5);
    }

    [Fact]
    public void An_Exit_Before_Entry_Still_Owes_The_First_Block_And_Bad_Inputs_Are_Safe()
    {
        var fee = FeeCalculator.CalculateSimulated(Hourly, Entry, Entry.AddSeconds(-30), null, 0);

        fee.Total.Should().Be(30_000);
        fee.DurationMinutes.Should().Be(0);
        var act = () => FeeCalculator.CalculateSimulated(null, Entry, Entry, Vietnam, 5);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void A_First_Block_Price_And_An_Entry_Fee_Are_Kept()
    {
        var tariff = Hourly with { FirstBlockMinutes = 120, FirstBlockPrice = 50_000, EntryFee = 2_000 };

        var fee = FeeCalculator.CalculateSimulated(tariff, Entry, Entry.AddSeconds(15), Vietnam, 5);

        fee.Total.Should().Be(2_000 + 50_000 + 2 * 30_000);
    }

    [Fact]
    public void A_First_Block_Needs_Both_Its_Length_And_Its_Price()
    {
        var noPrice = Hourly with { FirstBlockMinutes = 120 };
        var noLength = Hourly with { FirstBlockMinutes = 0, FirstBlockPrice = 50_000 };

        FeeCalculator.CalculateSimulated(noPrice, Entry, Entry.AddSeconds(5), Vietnam, 5).Total.Should().Be(30_000);
        FeeCalculator.CalculateSimulated(noLength, Entry, Entry.AddSeconds(5), Vietnam, 5).Total.Should().Be(30_000);
    }

    [Fact]
    public void Per_Entry_Charges_The_Entry_Fee_Once()
    {
        var tariff = new Tariff { ChargeModel = "PerEntry", EntryFee = 25_000 };

        var fee = FeeCalculator.CalculateSimulated(tariff, Entry, Entry.AddMinutes(10), Vietnam, 5);

        fee.Total.Should().Be(25_000);
        fee.Days.Single().Blocks.Should().Be(1);
    }

    [Fact]
    public void Day_And_Night_Price_Each_Block_By_Its_Start_Time()
    {
        // 17:59:55 local (UTC+7): the first block is a day block, the second starts at 18:00:00 (night)
        var tariff = new Tariff
        {
            ChargeModel = "DayNight", BlockMinutes = 60, BlockPrice = 1, DayStart = new TimeOnly(6, 0), NightStart = new TimeOnly(18, 0),
            DayPrice = 20_000, NightPrice = 40_000
        };
        var entry = new DateTime(2026, 10, 20, 10, 59, 55, DateTimeKind.Utc);

        var fee = FeeCalculator.CalculateSimulated(tariff, entry, entry.AddSeconds(10), Vietnam, 5);

        fee.Total.Should().Be(60_000);
    }

    // ---- settings ---------------------------------------------------------------------------------

    [Fact]
    public async Task Off_Until_The_Admin_Turns_It_On_And_The_Normal_Values_Are_Shown()
    {
        var t = new OperationsTestContext();

        var current = await Service(t).GetAsync();

        current.Enabled.Should().BeFalse();
        current.BlockSeconds.Should().Be(5);
        current.UpdatedAt.Should().BeNull();
        current.Normal.Should().BeEquivalentTo(new ParkingSimulationDefaultsModel
        {
            DepositHoldMinutes = 30, HoldMinutes = 15, DepositPaymentMinutes = 10,
            EarlyArrivalMinutes = 15, CancelRefundMinutes = 30, ExitWindowMinutes = 15
        });
    }

    [Fact]
    public async Task Missing_Settings_Read_As_Off()
    {
        var t = new OperationsTestContext();
        t.Options.Simulation = null;

        (await Service(t).GetAsync()).Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Saving_Applies_At_Once_And_Is_Stored()
    {
        var t = new OperationsTestContext();

        var saved = await Service(t).UpdateAsync(On(blockSeconds: 7), "admin-1");

        saved.Enabled.Should().BeTrue();
        saved.BlockSeconds.Should().Be(7);
        saved.DepositHoldSeconds.Should().Be(20);
        saved.HoldSeconds.Should().Be(15);
        saved.DepositPaymentSeconds.Should().Be(10);
        saved.EarlyArrivalSeconds.Should().Be(7);
        saved.CancelRefundSeconds.Should().Be(3);
        saved.ExitWindowSeconds.Should().Be(4);
        saved.UpdatedBy.Should().Be("admin-1");
        saved.UpdatedAt.Should().Be(t.Now);
        t.Options.IsSimulating.Should().BeTrue();
        t.Options.Simulation.BlockSeconds.Should().Be(7);
        var row = t.Db.SystemSettings.Single(s => s.SettingKey == ParkingSimulationService.SettingKey);
        row.Category.Should().Be("Parking");
        row.ValueType.Should().Be("json");

        // Saving again updates the same row
        await Service(t).UpdateAsync(Off(), "admin-2");
        t.Db.SystemSettings.Count(s => s.SettingKey == ParkingSimulationService.SettingKey).Should().Be(1);
        t.Options.IsSimulating.Should().BeFalse();
        (await Service(t).GetAsync()).UpdatedBy.Should().Be("admin-2");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    public async Task Times_Outside_1_To_3600_Seconds_Are_Refused(int seconds)
    {
        var t = new OperationsTestContext();
        var model = On();
        model.ExitWindowSeconds = seconds;

        var act = () => Service(t).UpdateAsync(model, "admin-1");

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*between 1 and 3600 seconds*");
        t.Options.IsSimulating.Should().BeFalse();
        t.Db.SystemSettings.Should().BeEmpty();
    }

    [Fact]
    public async Task Saving_Needs_A_Model()
    {
        var act = () => Service(new OperationsTestContext()).UpdateAsync(null, "admin-1");

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task The_Saved_Simulation_Comes_Back_After_A_Restart()
    {
        var t = new OperationsTestContext();
        await Service(t).UpdateAsync(On(blockSeconds: 9), "admin-1");
        t.Options.Simulation = new ParkingSimulationSettings();

        await Service(t).LoadAsync();

        t.Options.IsSimulating.Should().BeTrue();
        t.Options.Simulation.BlockSeconds.Should().Be(9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("not json")]
    public async Task Nothing_Readable_Saved_Means_Off(string value)
    {
        var t = new OperationsTestContext();
        if (value != null)
            t.Db.SystemSettings.Add(new SystemSetting { SettingKey = ParkingSimulationService.SettingKey, SettingValue = value, ValueType = "json" });
        await t.Db.SaveChangesAsync();
        t.Options.Simulation = new ParkingSimulationSettings { Enabled = true };

        await Service(t).LoadAsync();

        t.Options.IsSimulating.Should().BeFalse();
    }

    // ---- bookings and gates -----------------------------------------------------------------------

    private static async Task<OperationsTestContext> SimulatingAsync(int holdSeconds = 15)
    {
        var t = await new OperationsTestContext().SeedAsync();
        await Service(t).UpdateAsync(On(holdSeconds: holdSeconds), "admin-1");
        return t;
    }

    [Fact]
    public async Task A_Booking_Without_Deposit_Is_A_No_Show_Seconds_After_Arrival()
    {
        var t = await SimulatingAsync(holdSeconds: 15);
        var slot = t.FreeCarSlot();
        var booked = await t.Reservations.CreateAsync(t.CustomerUserId, new CreateReservationModel
        {
            ParkingLotId = t.Lot.Id, SlotId = slot.Id, VehicleId = t.Car.Id, PayDeposit = false,
            ArrivalTime = t.Clock.GetUtcNow().AddMinutes(1)
        });

        booked.HoldSeconds.Should().Be(15);
        booked.HoldMinutes.Should().Be(0);
        booked.CheckInFrom.Should().Be(booked.ArrivalTime.AddSeconds(-7));

        t.Clock.Advance(TimeSpan.FromSeconds(74));
        (await t.Jobs.RunSimulationTickAsync()).NoShows.Should().Be(0);

        t.Clock.Advance(TimeSpan.FromSeconds(1));
        var run = await t.Jobs.RunSimulationTickAsync();

        run.NoShows.Should().Be(1);
        (await t.Db.Reservations.SingleAsync(r => r.Id == booked.Id)).Status.Should().Be(ReservationStatus.NoShow);
        t.Db.Slots.Single(s => s.Id == slot.Id).Status.Should().Be(SlotStatus.Available);
    }

    [Fact]
    public async Task An_Unpaid_Deposit_Expires_Seconds_After_Booking()
    {
        var t = await SimulatingAsync();
        var booked = await t.Reservations.CreateAsync(t.CustomerUserId, new CreateReservationModel
        {
            ParkingLotId = t.Lot.Id, SlotId = t.FreeCarSlot().Id, VehicleId = t.Car.Id, ArrivalTime = t.Clock.GetUtcNow().AddMinutes(5)
        });
        booked.DepositDueAt.Should().Be(t.Now.AddSeconds(10));

        t.Clock.Advance(TimeSpan.FromSeconds(10));
        var run = await t.Jobs.RunSimulationTickAsync();

        run.ExpiredReservations.Should().Be(1);
        (await t.Db.Reservations.SingleAsync(r => r.Id == booked.Id)).Status.Should().Be(ReservationStatus.Expired);
    }

    [Fact]
    public async Task The_Tick_Does_Nothing_While_The_Simulation_Is_Off()
    {
        var t = await new OperationsTestContext().SeedAsync();

        (await t.Jobs.RunSimulationTickAsync()).Should().BeNull();
    }

    [Fact]
    public async Task The_Gate_Charges_Each_Simulated_Block_Without_A_Cap()
    {
        var t = await SimulatingAsync();
        var entry = await t.Gates.CheckInAsync(t.Lot.Id, new CheckInModel { PlateNumber = "30E-555.55", VehicleTypeId = t.TypeId("CAR_4") });
        await t.Gates.ConfirmEntryAsync(entry.Session.Id);

        t.Clock.Advance(TimeSpan.FromSeconds(12));
        var quote = await t.Gates.RequestPaymentAsync(entry.Session.Id, new CheckoutQuoteRequestModel());

        // Default car tariff: 20,000 VND per block; 12 seconds = 3 blocks of 5 seconds
        quote.Fee.Should().Be(60_000);

        // Turned off, the same stay is priced by the hour again
        await Service(t).UpdateAsync(Off(), "admin-1");
        var normal = await t.Gates.RequestPaymentAsync(entry.Session.Id, new CheckoutQuoteRequestModel());
        normal.Fee.Should().Be(20_000);
    }
}

