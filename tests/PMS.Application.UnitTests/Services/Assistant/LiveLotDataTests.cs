using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PMS.Application.Models.ParkingLot;
using PMS.Application.Models.Pricing;
using PMS.Application.Services;
using PMS.Application.Services.Assistant;
using Xunit;

namespace PMS.Application.UnitTests.Services.Assistant;

public class LiveLotDataTests
{
    private readonly IParkingLotService _lots = Substitute.For<IParkingLotService>();
    private readonly IPricingRuleService _pricing = Substitute.For<IPricingRuleService>();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 13, 2, 0, 0, TimeSpan.Zero));
    private readonly LiveLotData _sut;

    public LiveLotDataTests()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _lots);
        services.AddScoped(_ => _pricing);
        var provider = services.BuildServiceProvider();
        _sut = new LiveLotData(provider.GetRequiredService<IServiceScopeFactory>(), _clock, NullLogger<LiveLotData>.Instance);

        _pricing.GetPublicPricesAsync(Arg.Any<int>()).Returns(new List<PublicLotPriceModel>
        {
            Price("CAR_4", "4-seat car", 30000),
            Price("CAR_7", "7-seat car", 40000),
            Price("MOTORBIKE", "Motorbike", 5000),
        });
    }

    private static PublicLotPriceModel Price(string code, string name, decimal block) => new()
    {
        VehicleTypeCode = code,
        VehicleTypeName = name,
        ChargeModel = "Block",
        BlockMinutes = 60,
        BlockPrice = block,
    };

    private static PublicParkingLotResponseModel Lot(int id, string name, params (string Family, int Total)[] bays) => new()
    {
        Id = id,
        Name = name,
        Address = "1 Cong Hoa",
        Is24Hours = true,
        IsBookable = true,
        BaysByVehicle = bays.Select(b => new PublicLotBaysModel { VehicleType = b.Family, TotalBays = b.Total, FreeBays = 1 }).ToList(),
    };

    private void LotsAre(params PublicParkingLotResponseModel[] lots) =>
        _lots.GetPublicLotsAsync(Arg.Any<string>()).Returns(lots.AsEnumerable());

    [Fact]
    public async Task GetAsync_ListsThePriceOfEveryLot()
    {
        LotsAre(Lot(1, "Cong Hoa Parking Lot", ("CAR", 20), ("MOTORBIKE", 50)), Lot(2, "Ben Thanh Parking Lot", ("CAR", 10)));

        var snapshot = await _sut.GetAsync(CancellationToken.None);

        snapshot.Lots.Should().Be(2);
        snapshot.Text.Should().Contain("Cong Hoa Parking Lot").And.Contain("Ben Thanh Parking Lot");
        snapshot.Text.Should().Contain("4-seat car: 30,000 VND per hour");
        snapshot.Text.Should().Contain("Motorbike: 5,000 VND per hour");
        snapshot.Text.Should().Contain("Bays in total: 20 car, 50 motorbike");
    }

    [Fact]
    public async Task GetAsync_ShowsOnlyThePricesOfVehicleTypesTheLotHasBaysFor()
    {
        LotsAre(Lot(1, "Motorbike Only Lot", ("MOTORBIKE", 80)));

        var text = (await _sut.GetAsync(CancellationToken.None)).Text;

        text.Should().Contain("Motorbike: 5,000 VND per hour");
        text.Should().NotContain("4-seat car").And.NotContain("7-seat car");
    }

    [Fact]
    public async Task GetAsync_ShowsBothCarTypes_BecauseTheyShareTheCarBays()
    {
        LotsAre(Lot(1, "Car Lot", ("CAR", 30)));

        var text = (await _sut.GetAsync(CancellationToken.None)).Text;

        text.Should().Contain("4-seat car: 30,000 VND").And.Contain("7-seat car: 40,000 VND");
        text.Should().NotContain("Motorbike:");
    }

    [Fact]
    public async Task GetAsync_ListsEveryPrice_WhenTheLotHasNoBaysYet()
    {
        LotsAre(Lot(1, "New Lot"));

        var text = (await _sut.GetAsync(CancellationToken.None)).Text;

        text.Should().Contain("Prices now (bays not set up yet)");
        text.Should().Contain("4-seat car").And.Contain("7-seat car").And.Contain("Motorbike");
    }

    [Fact]
    public void AppendPrices_SaysSo_WhenTheLotHasNoPrice()
    {
        var text = new StringBuilder();

        LiveLotData.AppendPrices(text, Lot(1, "Empty Lot", ("CAR", 5)), []);

        text.ToString().Should().Contain("no price is set for this lot yet");
    }

    [Fact]
    public async Task GetAsync_ReusesTheListWithinAMinute_WhenNothingChanged()
    {
        LotsAre(Lot(1, "Old Name", ("CAR", 5)));
        await _sut.GetAsync(CancellationToken.None);

        LotsAre(Lot(1, "New Name", ("CAR", 5)));
        _clock.Advance(TimeSpan.FromSeconds(30));
        var text = (await _sut.GetAsync(CancellationToken.None)).Text;

        text.Should().Contain("Old Name");
    }

    [Fact]
    public async Task GetAsync_ReadsTheLotsAgainAtOnce_AfterALotOrPriceIsSaved()
    {
        LotsAre(Lot(1, "Cong Hoa Parking Lot", ("CAR", 5)));
        var before = await _sut.GetAsync(CancellationToken.None);

        // An Admin adds a lot: no waiting for the one-minute cache
        LotsAre(Lot(1, "Cong Hoa Parking Lot", ("CAR", 5)), Lot(2, "Tan Binh Parking Lot", ("MOTORBIKE", 40)));
        _sut.LotDataChanged(2);
        var after = await _sut.GetAsync(CancellationToken.None);

        after.Text.Should().Contain("Tan Binh Parking Lot");
        after.Version.Should().NotBe(before.Version, "cached answers are keyed by the version and must not quote the old list");
    }

    [Fact]
    public async Task GetAsync_ReadsTheLotsAgain_AfterAMinute()
    {
        LotsAre(Lot(1, "Old Name", ("CAR", 5)));
        await _sut.GetAsync(CancellationToken.None);

        LotsAre(Lot(1, "New Name", ("CAR", 5)));
        _clock.Advance(TimeSpan.FromMinutes(1));
        var text = (await _sut.GetAsync(CancellationToken.None)).Text;

        text.Should().Contain("New Name");
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
