using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PMS.API;
using PMS.Api.IntegrationTests.Common;
using PMS.Api.IntegrationTests.Helpers;
using PMS.Application.Helpers;
using PMS.Application.Models;
using PMS.Application.Models.Reports;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

public class ReportsEndpointTests
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    private async Task<(HttpClient client, ApplicationUser manager, ParkingLot lot)> CreateManagerWithLotAsync()
    {
        using var scope = _factory.Services.CreateScope();
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
            Address = "123 Test Street",
            Status = "Active",
            ManagerUserId = manager.Id,
            TimeZoneId = "SE Asia Standard Time",
            CreatedAt = DateTime.UtcNow
        };
        db.ParkingLots.Add(lot);
        await db.SaveChangesAsync();

        var client = _factory.CreateClient();
        var token = JwtHelper.GenerateToken(manager, config);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (client, manager, lot);
    }

    [Fact]
    public async Task AdminReport_AsAdmin_ReturnsOk_WithAdminRevenueResponse()
    {
        var adminClient = await _factory.CreateClientWithRoleAsync("Admin");

        var response = await adminClient.GetAsync("/api/admin/reports/revenue?startDate=2026-01-01&endDate=2026-01-31");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var apiResult = await ResponseHelper.GetApiResultAsync<AdminRevenueReportResponse>(response);
        apiResult.Should().NotBeNull();
        apiResult.Succeeded.Should().BeTrue();
        apiResult.Result.Should().NotBeNull();
        apiResult.Result.PaymentMethods.Should().NotBeEmpty();
    }

    [Fact]
    public async Task AdminReport_WithNonexistentLot_ReturnsNotFound()
    {
        var adminClient = await _factory.CreateClientWithRoleAsync("Admin");

        var response = await adminClient.GetAsync("/api/admin/reports/revenue?startDate=2026-01-01&endDate=2026-01-31&parkingLotId=99999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AdminReport_WithInvalidDateFormat_ReturnsBadRequest()
    {
        var adminClient = await _factory.CreateClientWithRoleAsync("Admin");

        var response = await adminClient.GetAsync("/api/admin/reports/revenue?startDate=bad-date&endDate=2026-01-31");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ManagerReport_AsManager_ReturnsOk_WithManagerRevenueResponse()
    {
        var (client, manager, lot) = await CreateManagerWithLotAsync();

        var response = await client.GetAsync($"/api/manager/reports/revenue?startDate=2026-01-01&endDate=2026-01-31&parkingLotId={lot.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var apiResult = await ResponseHelper.GetApiResultAsync<ManagerRevenueReportResponse>(response);
        apiResult.Should().NotBeNull();
        apiResult.Succeeded.Should().BeTrue();
        apiResult.Result.ManagerUserId.Should().Be(manager.Id);
        apiResult.Result.ByLot.Should().HaveCount(1);
        apiResult.Result.ByLot[0].ParkingLotId.Should().Be(lot.Id);
    }

    [Fact]
    public async Task ManagerReport_RequestingOtherLot_ReturnsForbidden()
    {
        var (client, _, _) = await CreateManagerWithLotAsync();

        // Querying lot 99999 or another manager's lot
        var response = await client.GetAsync("/api/manager/reports/revenue?startDate=2026-01-01&endDate=2026-01-31&parkingLotId=99999");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ManagerReport_Unauthenticated_ReturnsUnauthorized()
    {
        var anonymousClient = _factory.CreateClient();

        var response = await anonymousClient.GetAsync("/api/manager/reports/revenue?startDate=2026-01-01&endDate=2026-01-31");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ManagerReport_AsStaff_ReturnsForbidden()
    {
        var staffClient = await _factory.CreateClientWithRoleAsync("Staff");

        var response = await staffClient.GetAsync("/api/manager/reports/revenue?startDate=2026-01-01&endDate=2026-01-31");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AdminReport_AsManager_ReturnsForbidden()
    {
        var managerClient = await _factory.CreateClientWithRoleAsync("Manager");

        var response = await managerClient.GetAsync("/api/admin/reports/revenue?startDate=2026-01-01&endDate=2026-01-31");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
