using FluentAssertions;
using PMS.Application.Models.Pricing;
using PMS.Application.Services;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>How a price is worded in the customers' price-change notifications, the same way for cars and motorbikes.</summary>
public class PriceDescriptionTests
{
    [Fact]
    public void Block_Price_With_A_First_Block_And_A_Daily_Cap()
    {
        var motorbike = new PricingRulePrices
        {
            ChargeModel = "Block", FirstBlockMinutes = 120, FirstBlockPrice = 5_000, BlockMinutes = 60, BlockPrice = 2_000, MaxDailyFee = 20_000
        };

        motorbike.Describe().Should().Be("5,000 VND for the first 2 hours, then 2,000 VND per hour, at most 20,000 VND a day");
    }

    [Fact]
    public void Plain_Blocks_Say_Hours_Or_Minutes()
    {
        new PricingRulePrices { ChargeModel = "Block", BlockMinutes = 240, BlockPrice = 5_000 }.Describe().Should().Be("5,000 VND per 4 hours");
        new PricingRulePrices { ChargeModel = "Block", BlockMinutes = 30, BlockPrice = 3_000 }.Describe().Should().Be("3,000 VND per 30 min");
        new PricingRulePrices { ChargeModel = "Block", FirstBlockMinutes = 60, FirstBlockPrice = 10_000, BlockMinutes = 60, BlockPrice = 5_000 }
            .Describe().Should().Be("10,000 VND for the first hour, then 5,000 VND per hour");
    }

    [Fact]
    public void Day_And_Night_Prices()
    {
        var car = new PricingRulePrices { ChargeModel = "DayNight", BlockMinutes = 60, DayPrice = 25_000, NightPrice = 15_000, MaxDailyFee = 200_000 };

        car.Describe().Should().Be("25,000 VND by day and 15,000 VND by night per hour, at most 200,000 VND a day");
    }

    [Fact]
    public void A_Fee_Per_Day_Says_Per_Day()
    {
        new PricingRulePrices { ChargeModel = "PerEntry", EntryFee = 5_000 }.Describe().Should().Be("5,000 VND per day");
    }

    [Fact]
    public void A_Motorbike_Notice_Names_The_Vehicle_Type_In_Title_And_Message()
    {
        var before = new PricingRulePrices { ChargeModel = "PerEntry", EntryFee = 5_000 };
        var after = new PricingRulePrices
        {
            ChargeModel = "Block", FirstBlockMinutes = 120, FirstBlockPrice = 5_000, BlockMinutes = 60, BlockPrice = 2_000, MaxDailyFee = 20_000
        };

        var notice = new LotPriceNotice(3, "Cong Hoa Parking Lot", "Motorbike", after.Describe(), before.Describe(), "rule-9");

        notice.Title.Should().Be("Motorbike price changed at Cong Hoa Parking Lot");
        notice.Message.Should().Be(
            "Price for Cong Hoa Parking Lot (Motorbike) changed from 5,000 VND per day to 5,000 VND for the first 2 hours, " +
            "then 2,000 VND per hour, at most 20,000 VND a day. Vehicles already parked keep the price they entered with.");
    }
}
