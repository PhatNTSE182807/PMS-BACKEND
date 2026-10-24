using System;
using System.Collections.Generic;
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
using PMS.Application.Models;
using PMS.Application.Models.Pricing;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

public class ManagerPricingEndpointTests
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    private async Task<(HttpClient client, ParkingLot lot, VehicleType carType)> CreateManagerWithLotAsync()
    {
        var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
        var config = _factory.Services.GetRequiredService<IConfiguration>();

        var tag = Guid.NewGuid().ToString("N")[..8];
        var manager = new ApplicationUser
        {
            EmailConfirmed = true,
            Email = $"mgr_{tag}@test.com",
            UserName = $"mgr_{tag}",
            Role = "Manager",
            IsActive = true
        };
        (await userManager.CreateAsync(manager, "Password.1!")).Succeeded.Should().BeTrue();

        var lot = new ParkingLot
        {
            Code = $"LOT_{tag}".ToUpperInvariant(),
            Name = $"Lot {tag}",
            Address = "123 Management Way",
            Status = "Active",
            ManagerUserId = manager.Id,
            TimeZoneId = "SE Asia Standard Time",
            CreatedAt = DateTime.UtcNow
        };
        db.ParkingLots.Add(lot);

        var carType = await db.VehicleTypes.FirstOrDefaultAsync(v => v.Code == "CAR_4");
        if (carType == null)
        {
            carType = new VehicleType { Code = "CAR_4", Name = "4-Seat Car", IsActive = true, SortOrder = 1 };
            db.VehicleTypes.Add(carType);
        }

        await db.SaveChangesAsync();

        var client = _factory.CreateClient();
        var token = JwtHelper.GenerateToken(manager, config);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (client, lot, carType);
    }

    [Fact]
    public async Task Endpoint_WhenUnauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateUnauthorizedClient();

        var response = await client.GetAsync("/api/manager/lots/1/pricing-rules");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Endpoint_WhenUserRoleUnauthorized_ReturnsForbidden()
    {
        var userClient = await _factory.CreateClientWithRoleAsync("User");

        var response = await userClient.GetAsync("/api/manager/lots/1/pricing-rules");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Endpoint_WhenAdmin_AccessesPermittedLot()
    {
        var (_, lot, _) = await CreateManagerWithLotAsync();
        var adminClient = await _factory.CreateDefaultClientAsync();

        var response = await adminClient.GetAsync($"/api/manager/lots/{lot.Id}/pricing-rules");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var apiResult = await ResponseHelper.GetApiResultAsync<IReadOnlyList<PricingRuleModel>>(response);
        CheckResponse.Succeeded(apiResult);
        apiResult.Result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetPricingRules_WhenManagerAssigned_ReturnsSuccess()
    {
        var (client, lot, _) = await CreateManagerWithLotAsync();

        var response = await client.GetAsync($"/api/manager/lots/{lot.Id}/pricing-rules");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var apiResult = await ResponseHelper.GetApiResultAsync<IReadOnlyList<PricingRuleModel>>(response);
        CheckResponse.Succeeded(apiResult);
        apiResult.Result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetPricingRules_WhenManagerNotAssigned_ReturnsForbidden()
    {
        var (clientA, _, _) = await CreateManagerWithLotAsync();
        var (_, lotB, _) = await CreateManagerWithLotAsync();

        // Manager A tries to access Lot B
        var response = await clientA.GetAsync($"/api/manager/lots/{lotB.Id}/pricing-rules");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreatePricingRule_WhenValid_ReturnsSuccessAndPersistsRule()
    {
        var (client, lot, carType) = await CreateManagerWithLotAsync();

        var model = new CreatePricingRuleModel
        {
            Name = "Standard Car Hourly",
            VehicleTypeId = carType.Id,
            ChargeModel = "Block",
            FirstBlockMinutes = 120,
            FirstBlockPrice = 35_000,
            BlockMinutes = 60,
            BlockPrice = 25_000,
            GraceMinutes = 15,
            MaxDailyFee = 350_000,
            OvernightSurcharge = 120_000,
            LostTicketFee = 150_000,
            Status = "Active"
        };

        var response = await client.PostAsync($"/api/manager/lots/{lot.Id}/pricing-rules", new JsonContent(model));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var apiResult = await ResponseHelper.GetApiResultAsync<PricingRuleModel>(response);
        CheckResponse.Succeeded(apiResult);
        apiResult.Result.Id.Should().BeGreaterThan(0);
        apiResult.Result.Name.Should().Be("Standard Car Hourly");
        apiResult.Result.VersionNo.Should().Be(1);
        apiResult.Result.VehicleTypeCode.Should().Be("CAR_4");
        apiResult.Result.Status.Should().Be("Active");
    }

    [Fact]
    public async Task GetPricingRuleById_WhenBelongsToLot_ReturnsSuccess()
    {
        var (client, lot, carType) = await CreateManagerWithLotAsync();

        // Create rule first
        var createResponse = await client.PostAsync($"/api/manager/lots/{lot.Id}/pricing-rules", new JsonContent(new CreatePricingRuleModel
        {
            Name = "Rule For GetById",
            VehicleTypeId = carType.Id,
            ChargeModel = "Block",
            BlockMinutes = 60,
            BlockPrice = 20_000
        }));
        var created = (await ResponseHelper.GetApiResultAsync<PricingRuleModel>(createResponse)).Result;

        var response = await client.GetAsync($"/api/manager/lots/{lot.Id}/pricing-rules/{created.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var apiResult = await ResponseHelper.GetApiResultAsync<PricingRuleModel>(response);
        CheckResponse.Succeeded(apiResult);
        apiResult.Result.Id.Should().Be(created.Id);
        apiResult.Result.Name.Should().Be("Rule For GetById");
    }

    [Fact]
    public async Task GetPricingRuleById_WhenRuleBelongsToAnotherLot_ReturnsNotFound()
    {
        var (clientA, lotA, carTypeA) = await CreateManagerWithLotAsync();
        var (clientB, lotB, carTypeB) = await CreateManagerWithLotAsync();

        // Manager B creates a rule on Lot B
        var createResponse = await clientB.PostAsync($"/api/manager/lots/{lotB.Id}/pricing-rules", new JsonContent(new CreatePricingRuleModel
        {
            Name = "Lot B Private Rule",
            VehicleTypeId = carTypeB.Id,
            ChargeModel = "Block"
        }));
        var createdRuleB = (await ResponseHelper.GetApiResultAsync<PricingRuleModel>(createResponse)).Result;

        // Manager A tries to fetch Lot B's rule through Lot A's endpoint
        var response = await clientA.GetAsync($"/api/manager/lots/{lotA.Id}/pricing-rules/{createdRuleB.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdatePricingRule_WhenValid_BumpsVersion()
    {
        var (client, lot, carType) = await CreateManagerWithLotAsync();

        // Create initial rule
        var createResponse = await client.PostAsync($"/api/manager/lots/{lot.Id}/pricing-rules", new JsonContent(new CreatePricingRuleModel
        {
            Name = "Version 1 Rule",
            VehicleTypeId = carType.Id,
            ChargeModel = "Block",
            BlockMinutes = 60,
            BlockPrice = 20_000
        }));
        var created = (await ResponseHelper.GetApiResultAsync<PricingRuleModel>(createResponse)).Result;

        // Update rule
        var updateModel = new UpdatePricingRuleModel
        {
            Name = "Version 2 Rule",
            VehicleTypeId = carType.Id,
            ChargeModel = "Block",
            BlockMinutes = 60,
            BlockPrice = 30_000,
            EffectiveFrom = DateTime.UtcNow,
            Status = "Draft",
            Reason = "Price increase"
        };

        var response = await client.PutAsync($"/api/manager/lots/{lot.Id}/pricing-rules/{created.Id}", new JsonContent(updateModel));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var apiResult = await ResponseHelper.GetApiResultAsync<PricingRuleModel>(response);
        CheckResponse.Succeeded(apiResult);
        apiResult.Result.VersionNo.Should().Be(2);
        apiResult.Result.BlockPrice.Should().Be(30_000);
        apiResult.Result.Name.Should().Be("Version 2 Rule");
    }

    [Fact]
    public async Task SetPricingRuleStatus_WhenActive_SetsApproved()
    {
        var (client, lot, carType) = await CreateManagerWithLotAsync();

        var createResponse = await client.PostAsync($"/api/manager/lots/{lot.Id}/pricing-rules", new JsonContent(new CreatePricingRuleModel
        {
            Name = "Draft to Activate",
            VehicleTypeId = carType.Id,
            ChargeModel = "Block",
            Status = "Draft"
        }));
        var created = (await ResponseHelper.GetApiResultAsync<PricingRuleModel>(createResponse)).Result;

        // Patch status to Active
        var response = await client.PatchAsync(
            $"/api/manager/lots/{lot.Id}/pricing-rules/{created.Id}/status",
            new JsonContent(new SetPricingRuleStatusModel { Status = "Active", Reason = "Going live" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var apiResult = await ResponseHelper.GetApiResultAsync<PricingRuleModel>(response);
        CheckResponse.Succeeded(apiResult);
        apiResult.Result.Status.Should().Be("Active");
        apiResult.Result.ApprovedBy.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DeletePricingRule_WhenValid_ArchivesRule()
    {
        var (client, lot, carType) = await CreateManagerWithLotAsync();

        var createResponse = await client.PostAsync($"/api/manager/lots/{lot.Id}/pricing-rules", new JsonContent(new CreatePricingRuleModel
        {
            Name = "Rule to Archive",
            VehicleTypeId = carType.Id,
            ChargeModel = "Block"
        }));
        var created = (await ResponseHelper.GetApiResultAsync<PricingRuleModel>(createResponse)).Result;

        var deleteResponse = await client.DeleteAsync($"/api/manager/lots/{lot.Id}/pricing-rules/{created.Id}");

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Fetch again to confirm status is Archived
        var getResponse = await client.GetAsync($"/api/manager/lots/{lot.Id}/pricing-rules/{created.Id}");
        var apiResult = await ResponseHelper.GetApiResultAsync<PricingRuleModel>(getResponse);
        apiResult.Result.Status.Should().Be("Archived");
    }

    [Fact]
    public async Task SimulatePricing_WhenValid_ReturnsCalculation()
    {
        var (client, lot, carType) = await CreateManagerWithLotAsync();

        var createResponse = await client.PostAsync($"/api/manager/lots/{lot.Id}/pricing-rules", new JsonContent(new CreatePricingRuleModel
        {
            Name = "Simulate Tariff",
            VehicleTypeId = carType.Id,
            ChargeModel = "Block",
            GraceMinutes = 15,
            FirstBlockMinutes = 120,
            FirstBlockPrice = 35_000,
            BlockMinutes = 60,
            BlockPrice = 25_000,
            MaxDailyFee = 350_000,
            LostTicketFee = 150_000,
            Status = "Active"
        }));
        var created = (await ResponseHelper.GetApiResultAsync<PricingRuleModel>(createResponse)).Result;

        var entry = new DateTime(2026, 10, 23, 8, 30, 0, DateTimeKind.Utc);
        var exit = new DateTime(2026, 10, 23, 15, 45, 0, DateTimeKind.Utc);

        var response = await client.PostAsync($"/api/manager/lots/{lot.Id}/pricing-rules/simulate", new JsonContent(new PricingSimulationRequestModel
        {
            RuleId = created.Id,
            EntryTime = entry,
            ExitTime = exit,
            DepositAmount = 50_000,
            IsLostTicket = false
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var apiResult = await ResponseHelper.GetApiResultAsync<PricingSimulationResultModel>(response);
        CheckResponse.Succeeded(apiResult);
        apiResult.Result.DurationMinutes.Should().Be(435);
        apiResult.Result.GrossFee.Should().Be(185_000);
        apiResult.Result.DepositApplied.Should().Be(50_000);
        apiResult.Result.AmountDue.Should().Be(135_000);
    }

    [Fact]
    public async Task CreatePricingRule_WhenInvalidData_ReturnsBadRequest()
    {
        var (client, lot, _) = await CreateManagerWithLotAsync();

        var invalidModel = new CreatePricingRuleModel
        {
            Name = "", // Empty name fails validation
            VehicleTypeId = 0,
            ChargeModel = "InvalidModel"
        };

        var response = await client.PostAsync($"/api/manager/lots/{lot.Id}/pricing-rules", new JsonContent(invalidModel));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var apiResult = await ResponseHelper.GetApiResultAsync<string>(response);
        apiResult.Succeeded.Should().BeFalse();
        apiResult.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetPricingRuleById_WhenNonExistent_ReturnsNotFound()
    {
        var (client, lot, _) = await CreateManagerWithLotAsync();

        var response = await client.GetAsync($"/api/manager/lots/{lot.Id}/pricing-rules/99999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
