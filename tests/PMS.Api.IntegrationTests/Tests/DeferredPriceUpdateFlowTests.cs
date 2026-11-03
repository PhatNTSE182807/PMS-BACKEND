using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PMS.API;
using PMS.Api.IntegrationTests.Common;
using PMS.Api.IntegrationTests.Helpers;
using PMS.Application.Helpers;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using PMS.Application.Models.Pricing;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

/// <summary>
/// The whole deferred price update through the HTTP API: a Manager changes a price, vehicles come and go at the gate,
/// and the new price goes live when the lot is empty.
/// </summary>
public class DeferredPriceUpdateFlowTests
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    private sealed record Lot(int Id, int CarTypeId, HttpClient Manager, PricingRuleModel Rule);

    private async Task<Lot> NewLotWithRuleAsync(decimal blockPrice)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
        var config = _factory.Services.GetRequiredService<IConfiguration>();

        var tag = Guid.NewGuid().ToString("N")[..8];
        var manager = new ApplicationUser { EmailConfirmed = true, Email = $"mgr_{tag}@test.com", UserName = $"mgr_{tag}", Role = "Manager", IsActive = true };
        (await userManager.CreateAsync(manager, "Password.1!")).Succeeded.Should().BeTrue();

        var carType = await db.VehicleTypes.FirstOrDefaultAsync(v => v.Code == "CAR_4");
        if (carType == null)
        {
            carType = new VehicleType { Code = "CAR_4", Name = "4-Seat Car", IsActive = true, SortOrder = 1 };
            db.VehicleTypes.Add(carType);
        }

        var lot = new ParkingLot
        {
            Code = $"LOT_{tag}".ToUpperInvariant(), Name = $"Lot {tag}", Address = "1 Test St", Status = "Active",
            ManagerUserId = manager.Id, TimeZoneId = "SE Asia Standard Time", CreatedAt = DateTime.UtcNow
        };
        db.ParkingLots.Add(lot);
        await db.SaveChangesAsync();
        await LotTemplates.ApplyAsync(db, lot, "L3S20", "admin");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", JwtHelper.GenerateToken(manager, config));

        var created = await client.PostAsync($"/api/manager/lots/{lot.Id}/pricing-rules", new JsonContent(new CreatePricingRuleModel
        {
            Name = "Car hourly", VehicleTypeId = carType.Id, ChargeModel = "Block", BlockMinutes = 60, BlockPrice = blockPrice, Status = "Active"
        }));
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var rule = (await ResponseHelper.GetApiResultAsync<PricingRuleModel>(created)).Result;
        return new Lot(lot.Id, carType.Id, client, rule);
    }

    private static UpdatePricingRuleModel Change(PricingRuleModel rule, decimal blockPrice) => new()
    {
        Name = rule.Name, VehicleTypeId = rule.VehicleTypeId, ChargeModel = "Block", BlockMinutes = 60, BlockPrice = blockPrice,
        EffectiveFrom = rule.EffectiveFrom, Status = "Active", Reason = "Price review"
    };

    private static async Task<PricingRuleModel> UpdateAsync(Lot lot, decimal blockPrice)
    {
        var response = await lot.Manager.PutAsync($"/api/manager/lots/{lot.Id}/pricing-rules/{lot.Rule.Id}", new JsonContent(Change(lot.Rule, blockPrice)));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await ResponseHelper.GetApiResultAsync<PricingRuleModel>(response)).Result;
    }

    private static async Task<PricingRuleModel> ReadRuleAsync(Lot lot) =>
        (await ResponseHelper.GetApiResultAsync<PricingRuleModel>(await lot.Manager.GetAsync($"/api/manager/lots/{lot.Id}/pricing-rules/{lot.Rule.Id}"))).Result;

    /// <summary>Check-in plus the loop sensor: the vehicle is inside.</summary>
    private static async Task<long> EnterAsync(Lot lot, string plate)
    {
        var entry = await lot.Manager.PostAsync($"/api/staff/lots/{lot.Id}/check-in", new JsonContent(new CheckInModel { PlateNumber = plate, VehicleTypeId = lot.CarTypeId }));
        entry.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await ResponseHelper.GetApiResultAsync<CheckInResultModel>(entry)).Result;
        result.Outcome.Should().Be("Created");
        (await lot.Manager.PostAsync($"/api/staff/sessions/{result.Session.Id}/entry-confirm", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        return result.Session.Id;
    }

    private static async Task<decimal> QuoteAsync(Lot lot, long sessionId, string plate)
    {
        var response = await lot.Manager.PostAsync($"/api/staff/sessions/{sessionId}/quote", new JsonContent(new CheckoutQuoteRequestModel { PlateNumber = plate }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await ResponseHelper.GetApiResultAsync<CheckoutQuoteModel>(response)).Result.AmountDue;
    }

    /// <summary>Quote, pay in cash and pass the exit barrier.</summary>
    private static async Task LeaveAsync(Lot lot, long sessionId, string plate)
    {
        var due = await QuoteAsync(lot, sessionId, plate);
        (await lot.Manager.PostAsync($"/api/staff/sessions/{sessionId}/payments/cash", new JsonContent(new CashPaymentModel { Amount = due + 1_000 }))).StatusCode.Should().Be(HttpStatusCode.OK);
        var exit = await lot.Manager.PostAsync($"/api/staff/sessions/{sessionId}/check-out", new JsonContent(new CheckoutModel { PlateNumber = plate }));
        exit.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ResponseHelper.GetApiResultAsync<CheckoutResultModel>(exit)).Result.BarrierOpen.Should().BeTrue();
    }

    private async Task<int> OccupiedBaysAsync(Lot lot)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PmsDbContext>().ParkingLots.AsNoTracking().Where(l => l.Id == lot.Id).Select(l => l.OccupiedBays).SingleAsync();
    }

    private async Task<int> WaitingChangesAsync(Lot lot)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PmsDbContext>().PendingPriceChanges.CountAsync(p => p.ParkingLotId == lot.Id);
    }

    private async Task<decimal> SnapshotPriceAsync(long sessionId)
    {
        using var scope = _factory.Services.CreateScope();
        var json = await scope.ServiceProvider.GetRequiredService<PmsDbContext>().ParkingSessions.AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.PriceSnapshotJson).SingleAsync();
        return TariffSnapshot.Read(json)!.BlockPrice;
    }

    [Fact]
    public async Task A_Price_Change_Waits_For_The_Last_Vehicle_And_Every_Stay_Keeps_Its_Own_Price()
    {
        var lot = await NewLotWithRuleAsync(20_000);

        // 1. Empty lot: live at once
        var applied = await UpdateAsync(lot, 25_000);
        applied.PriceUpdateStatus.Should().Be(PriceUpdateStatuses.Applied);
        applied.VersionNo.Should().Be(2);
        (await UpdateAsync(lot, 20_000)).PriceUpdateStatus.Should().Be(PriceUpdateStatuses.Applied);

        // 2. Two cars come in at 20,000
        var carA = await EnterAsync(lot, "51A-111.11");
        var carB = await EnterAsync(lot, "51B-222.22");
        (await OccupiedBaysAsync(lot)).Should().Be(2);
        (await SnapshotPriceAsync(carA)).Should().Be(20_000);

        // 3. Vehicles inside: the new price waits, a second request replaces it
        var pending = await UpdateAsync(lot, 30_000);
        pending.PriceUpdateStatus.Should().Be(PriceUpdateStatuses.Pending);
        pending.BlockPrice.Should().Be(20_000);
        pending.PendingPrice.Prices.BlockPrice.Should().Be(30_000);
        var replaced = await UpdateAsync(lot, 35_000);
        replaced.PendingPrice.RevisionNo.Should().Be(2);
        (await WaitingChangesAsync(lot)).Should().Be(1);
        (await UpdateAsync(lot, 20_000)).PriceUpdateStatus.Should().Be(PriceUpdateStatuses.Unchanged);
        (await WaitingChangesAsync(lot)).Should().Be(0, "asking for the live price withdraws the waiting one");
        (await UpdateAsync(lot, 30_000)).PriceUpdateStatus.Should().Be(PriceUpdateStatuses.Pending);

        // 4. Cars inside are still charged the price they entered with
        (await QuoteAsync(lot, carA, "51A-111.11")).Should().Be(20_000);

        // 5. First car leaves: one is still inside, nothing changes
        await LeaveAsync(lot, carA, "51A-111.11");
        (await OccupiedBaysAsync(lot)).Should().Be(1);
        (await ReadRuleAsync(lot)).BlockPrice.Should().Be(20_000);
        (await WaitingChangesAsync(lot)).Should().Be(1);

        // 6. Last car leaves (still paying the old price): the waiting price goes live
        await LeaveAsync(lot, carB, "51B-222.22");
        (await OccupiedBaysAsync(lot)).Should().Be(0);
        var live = await ReadRuleAsync(lot);
        live.BlockPrice.Should().Be(30_000);
        live.PendingPrice.Should().BeNull();
        (await WaitingChangesAsync(lot)).Should().Be(0);

        // 7. A car coming in now gets the new price
        var carC = await EnterAsync(lot, "51C-333.33");
        (await SnapshotPriceAsync(carC)).Should().Be(30_000);
        (await QuoteAsync(lot, carC, "51C-333.33")).Should().Be(30_000);
    }

    [Fact]
    public async Task A_Vehicle_Turned_Back_At_The_Barrier_Counts_Out_And_Lets_The_Waiting_Price_Through()
    {
        var lot = await NewLotWithRuleAsync(20_000);
        var entry = await lot.Manager.PostAsync($"/api/staff/lots/{lot.Id}/check-in", new JsonContent(new CheckInModel { PlateNumber = "51A-444.44", VehicleTypeId = lot.CarTypeId }));
        var sessionId = (await ResponseHelper.GetApiResultAsync<CheckInResultModel>(entry)).Result.Session.Id;
        (await OccupiedBaysAsync(lot)).Should().Be(1, "the barrier is open, so the bay is spoken for");
        (await UpdateAsync(lot, 30_000)).PriceUpdateStatus.Should().Be(PriceUpdateStatuses.Pending);

        (await lot.Manager.PostAsync($"/api/staff/sessions/{sessionId}/entry-cancel", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await OccupiedBaysAsync(lot)).Should().Be(0);
        (await ReadRuleAsync(lot)).BlockPrice.Should().Be(30_000);
    }

    [Fact]
    public async Task A_Manager_Cannot_Change_A_Shared_Rule_But_An_Admin_Can()
    {
        var lot = await NewLotWithRuleAsync(20_000);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
            (await db.PricingRules.SingleAsync(r => r.Id == lot.Rule.Id)).ParkingLotId = null;
            await db.SaveChangesAsync();
        }

        var refused = await lot.Manager.PutAsync($"/api/manager/lots/{lot.Id}/pricing-rules/{lot.Rule.Id}", new JsonContent(Change(lot.Rule, 30_000)));
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ResponseHelper.GetApiResultAsync<string>(refused)).Errors.Should().ContainSingle().Which.Should().Contain("shared pricing rule");

        var admin = await _factory.CreateDefaultClientAsync();
        var allowed = await admin.PutAsync($"/api/manager/lots/{lot.Id}/pricing-rules/{lot.Rule.Id}", new JsonContent(Change(lot.Rule, 30_000)));
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ResponseHelper.GetApiResultAsync<PricingRuleModel>(allowed)).Result.PriceUpdateStatus.Should().Be(PriceUpdateStatuses.Applied);
    }
}
