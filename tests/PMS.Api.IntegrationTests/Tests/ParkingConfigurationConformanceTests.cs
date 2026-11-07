using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PMS.API;
using PMS.Api.IntegrationTests.Common;
using PMS.Api.IntegrationTests.Helpers;
using PMS.Application.Models;
using PMS.Application.Models.ParkingLot;
using PMS.Application.Models.Slot;
using PMS.Application.Models.Zone;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

public class ParkingConfigurationConformanceTests
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    private async Task<int> CreateTestLotAsync()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var code = $"L_{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        var lotResponse = await adminClient.PostAsync("/api/lots", new JsonContent(new CreateParkingLotModel
        {
            Code = code,
            Name = "Conformance Test Lot",
            Address = "100 Test Blvd",
            LayoutTemplate = "L3S20",
        }));
        var lotResult = await ResponseHelper.GetApiResultAsync<ParkingLotResponseModel>(lotResponse);
        return lotResult.Result.Id;
    }

    [Fact]
    public async Task Lots_RBAC_Matrix_Should_Be_Enforced()
    {
        var unauthClient = _factory.CreateUnauthorizedClient();
        var userClient = await _factory.CreateClientWithRoleAsync("User");
        var managerClient = await _factory.CreateClientWithRoleAsync("Manager");
        var adminClient = await _factory.CreateDefaultClientAsync();

        var lotId = await CreateTestLotAsync();
        var model = new CreateParkingLotModel
        {
            Code = $"L_{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "RBAC Lot",
            Address = "123 RBAC",
            LayoutTemplate = "L3S20",
        };

        // 1. POST /api/lots: Admin only (Manager/User/Anon rejected)
        (await unauthClient.PostAsync("/api/lots", new JsonContent(model))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await userClient.PostAsync("/api/lots", new JsonContent(model))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await managerClient.PostAsync("/api/lots", new JsonContent(model))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 2. DELETE /api/lots/{id}: Admin only (Manager/User/Anon rejected)
        (await unauthClient.DeleteAsync($"/api/lots/{lotId}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await userClient.DeleteAsync($"/api/lots/{lotId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await managerClient.DeleteAsync($"/api/lots/{lotId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 3. GET /api/lots: Admin & Manager allowed, User & Anon rejected
        (await unauthClient.GetAsync("/api/lots")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await userClient.GetAsync("/api/lots")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await managerClient.GetAsync("/api/lots")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await adminClient.GetAsync("/api/lots")).StatusCode.Should().Be(HttpStatusCode.OK);

        // 4. GET /api/public/lots: Anon allowed
        (await unauthClient.GetAsync("/api/public/lots")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PublicLots_Should_Not_Leak_Internal_Management_Fields()
    {
        var unauthClient = _factory.CreateUnauthorizedClient();

        var response = await unauthClient.GetAsync("/api/public/lots");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var rawJson = (await response.Content.ReadAsStringAsync()).ToLowerInvariant();
        rawJson.Should().NotContain("manageruserid");
        rawJson.Should().NotContain("partnerid");
        rawJson.Should().NotContain("createdby");

    }

    [Fact]
    public async Task CreateLot_WhenDuplicateCode_Should_Return_BadRequest()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var code = $"L_DUP_{Guid.NewGuid():N}"[..8].ToUpperInvariant();

        var first = await adminClient.PostAsync("/api/lots", new JsonContent(new CreateParkingLotModel
        {
            Code = code,
            Name = "Lot A",
            Address = "Address A",
            LayoutTemplate = "L3S20",
        }));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await adminClient.PostAsync("/api/lots", new JsonContent(new CreateParkingLotModel
        {
            Code = code,
            Name = "Lot B",
            Address = "Address B",
            LayoutTemplate = "L3S20",
        }));
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteLot_WhenHasActiveSessions_Should_Return_BadRequest()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var lotId = await CreateTestLotAsync();
        // Close the lot first, so the vehicle inside is the only reason the delete is refused
        (await adminClient.PatchAsync($"/api/lots/{lotId}/status", new JsonContent(new UpdateParkingLotStatusModel
        {
            Status = "TemporarilyClosed",
            ClosureReason = "Lot removed"
        }))).StatusCode.Should().Be(HttpStatusCode.OK);

        var pmsContext = _factory.GetRequiredService<PmsDbContext>();
        pmsContext.ParkingSessions.Add(new ParkingSession
        {
            ParkingLotId = lotId,
            SessionCode = $"SESS_{Guid.NewGuid():N}"[..10],
            Status = "Active",
            EntryPlateNumber = "29A-99999",
            EntryPlateNormalized = "29A99999",
            EntryTime = DateTime.UtcNow,
            RowVersion = Array.Empty<byte>()
        });
        await pmsContext.SaveChangesAsync();

        var deleteResponse = await adminClient.DeleteAsync($"/api/lots/{lotId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>The first generated slot of a lot (lots are built from a ready-made model).</summary>
    private async Task<Slot> FirstSlotAsync(int lotId)
    {
        var pmsContext = _factory.GetRequiredService<PmsDbContext>();
        return await pmsContext.Slots.Include(s => s.Zone)
            .Where(s => s.Zone.ParkingLotId == lotId)
            .OrderBy(s => s.Code)
            .FirstAsync();
    }

    [Fact]
    public async Task CreateLot_Without_A_Model_Should_Return_BadRequest()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();

        var missing = await adminClient.PostAsync("/api/lots", new JsonContent(new CreateParkingLotModel
        {
            Name = "No Model Lot",
            Address = "1 Nowhere"
        }));
        var unknown = await adminClient.PostAsync("/api/lots", new JsonContent(new CreateParkingLotModel
        {
            Name = "Custom Model Lot",
            Address = "1 Nowhere",
            LayoutTemplate = "MY_OWN_3D"
        }));

        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateLot_Should_Generate_Zones_And_Slots_From_The_Model()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var lotId = await CreateTestLotAsync();

        var lot = await ResponseHelper.GetApiResultAsync<ParkingLotResponseModel>(await adminClient.GetAsync($"/api/lots/{lotId}"));
        lot.Result.LayoutTemplate.Should().Be("L3S20");
        lot.Result.Capacity.Should().Be(60);
        lot.Result.TotalZones.Should().Be(3);
        lot.Result.TotalSlots.Should().Be(60);
    }

    [Fact]
    public async Task Zone_And_Slot_Structure_Cannot_Be_Edited_By_Hand()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var lotId = await CreateTestLotAsync();
        var slot = await FirstSlotAsync(lotId);

        // Create and delete endpoints are gone: the structure comes from the lot's model
        (await adminClient.PostAsync($"/api/lots/{lotId}/zones", new JsonContent(new CreateZoneModel { Name = "Extra", Capacity = 5 })))
            .StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        (await adminClient.DeleteAsync($"/api/zones/{slot.ZoneId}")).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        (await adminClient.PostAsync($"/api/zones/{slot.ZoneId}/slots", new JsonContent(new CreateSlotModel { Code = "EXTRA" })))
            .StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        (await adminClient.DeleteAsync($"/api/slots/{slot.Id}")).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Managers_Should_Not_Configure_Lots_Zones_Or_Slots()
    {
        var managerClient = await _factory.CreateClientWithRoleAsync("Manager");
        var lotId = await CreateTestLotAsync();
        var slot = await FirstSlotAsync(lotId);

        (await managerClient.PutAsync($"/api/lots/{lotId}", new JsonContent(new UpdateParkingLotModel { Name = "X", Address = "Y", Is24Hours = true })))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await managerClient.PutAsync($"/api/lots/{lotId}/template", new JsonContent(new { layoutTemplate = "L4S30" })))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await managerClient.PutAsync($"/api/zones/{slot.ZoneId}", new JsonContent(new UpdateZoneModel { Name = "X", Capacity = 20 })))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await managerClient.PutAsync($"/api/slots/{slot.Id}", new JsonContent(new UpdateSlotModel { Code = "X" })))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Operating the lot is still theirs: a bay can go into maintenance
        (await managerClient.PatchAsync($"/api/slots/{slot.Id}/status", new JsonContent(new UpdateSlotStatusModel { Status = "Maintenance" })))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ApplyTemplate_To_A_Lot_With_Zones_Should_Return_BadRequest()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var lotId = await CreateTestLotAsync();

        var response = await adminClient.PutAsync($"/api/lots/{lotId}/template", new JsonContent(new { layoutTemplate = "L4S30" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Slot_UpdateStatus_WhenInvalid_Should_Return_BadRequest()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var lotId = await CreateTestLotAsync();
        var slot = await FirstSlotAsync(lotId);

        var badStatusResponse = await adminClient.PatchAsync($"/api/slots/{slot.Id}/status", new JsonContent(new UpdateSlotStatusModel
        {
            Status = "NonExistentStatus123"
        }));
        badStatusResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateParkingLot_Without_Code_Should_AutoGenerate_Code_And_Succeed()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var response = await adminClient.PostAsync("/api/lots", new JsonContent(new CreateParkingLotModel
        {
            Name = $"Auto Code Lot {Guid.NewGuid():N}"[..20],
            Address = "123 Auto Street",
            LayoutTemplate = "L3S20",
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ResponseHelper.GetApiResultAsync<ParkingLotResponseModel>(response);
        result.Succeeded.Should().BeTrue();
        result.Result.Code.Should().StartWith("LOT-");
    }

    [Fact]
    public async Task ParkingLot_Route_Alias_Should_Support_Api_ParkingLots()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var response = await adminClient.GetAsync("/api/parking-lots");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var publicResponse = await _factory.CreateUnauthorizedClient().GetAsync("/api/public/parking-lots");
        publicResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
