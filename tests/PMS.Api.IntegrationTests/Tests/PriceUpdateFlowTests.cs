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
using PMS.Application.Jobs;
using PMS.Application.Models.Operations;
using PMS.Application.Models.Pricing;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

/// <summary>
/// A price update through the HTTP API: a Manager changes a price while vehicles come and go at the gate. The new
/// price is live at once for vehicles entering afterwards; those already inside keep theirs.
/// </summary>
public class PriceUpdateFlowTests
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

    private async Task<decimal> SnapshotPriceAsync(long sessionId)
    {
        using var scope = _factory.Services.CreateScope();
        var json = await scope.ServiceProvider.GetRequiredService<PmsDbContext>().ParkingSessions.AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.PriceSnapshotJson).SingleAsync();
        return TariffSnapshot.Read(json)!.BlockPrice;
    }

    [Fact]
    public async Task A_Price_Change_Is_Live_At_Once_And_Every_Stay_Keeps_The_Price_It_Entered_With()
    {
        var lot = await NewLotWithRuleAsync(20_000);
        var inside = await EnterAsync(lot, "51A-111.11");
        (await SnapshotPriceAsync(inside)).Should().Be(20_000);

        var updated = await UpdateAsync(lot, 30_000);
        updated.PriceUpdateStatus.Should().Be(PriceUpdateStatuses.Applied);
        updated.BlockPrice.Should().Be(30_000);
        updated.VersionNo.Should().Be(2);

        (await QuoteAsync(lot, inside, "51A-111.11")).Should().Be(20_000);

        var after = await EnterAsync(lot, "51C-333.33");
        (await SnapshotPriceAsync(after)).Should().Be(30_000);
        (await QuoteAsync(lot, after, "51C-333.33")).Should().Be(30_000);
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

    [Fact]
    public async Task A_Price_Change_Reaches_Every_Active_Customers_Notifications()
    {
        var lot = await NewLotWithRuleAsync(20_000);
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var customer = new ApplicationUser { EmailConfirmed = true, Email = $"cus_{tag}@test.com", UserName = $"cus_{tag}", Role = "User", IsActive = true };
        var inactive = new ApplicationUser { EmailConfirmed = true, Email = $"off_{tag}@test.com", UserName = $"off_{tag}", Role = "User", IsActive = false };
        (await userManager.CreateAsync(customer, "Password.1!")).Succeeded.Should().BeTrue();
        (await userManager.CreateAsync(inactive, "Password.1!")).Succeeded.Should().BeTrue();

        await UpdateAsync(lot, 30_000);

        // The test host has no Hangfire server, so the job the update queued is run here
        var jobs = ActivatorUtilities.CreateInstance<NotificationJobs>(scope.ServiceProvider);
        await jobs.LotPriceChangedAsync("Price changed", "Changed", $"test-{tag}", lot.Id, "Lot");

        var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
        var told = await db.UserNotifications.AsNoTracking().Where(n => n.RefId == $"test-{tag}").Select(n => n.UserId).ToListAsync();
        told.Should().Contain(customer.Id).And.NotContain(inactive.Id);
    }
}
