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
            Capacity = 200
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
            Capacity = 50
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
            Capacity = 50
        }));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await adminClient.PostAsync("/api/lots", new JsonContent(new CreateParkingLotModel
        {
            Code = code,
            Name = "Lot B",
            Address = "Address B",
            Capacity = 60
        }));
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteLot_WhenHasActiveSessions_Should_Return_BadRequest()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var lotId = await CreateTestLotAsync();

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

    [Fact]
    public async Task CreateZone_Composite_Code_Uniqueness_Should_Work()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var lot1Id = await CreateTestLotAsync();
        var lot2Id = await CreateTestLotAsync();

        var zoneCode = "ZA";

        // 1. Create ZA in Lot 1 -> Success
        var first = await adminClient.PostAsync($"/api/lots/{lot1Id}/zones", new JsonContent(new CreateZoneModel
        {
            ParkingLotId = lot1Id,
            Code = zoneCode,
            Name = "Zone A Lot 1",
            Capacity = 20
        }));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. Create duplicate ZA in Lot 1 -> BadRequest
        var duplicateInSameLot = await adminClient.PostAsync($"/api/lots/{lot1Id}/zones", new JsonContent(new CreateZoneModel
        {
            ParkingLotId = lot1Id,
            Code = zoneCode,
            Name = "Zone A Lot 1 Duplicate",
            Capacity = 20
        }));
        duplicateInSameLot.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 3. Create ZA in Lot 2 -> Success (composite uniqueness allows same code across lots)
        var sameCodeInDifferentLot = await adminClient.PostAsync($"/api/lots/{lot2Id}/zones", new JsonContent(new CreateZoneModel
        {
            ParkingLotId = lot2Id,
            Code = zoneCode,
            Name = "Zone A Lot 2",
            Capacity = 25
        }));
        sameCodeInDifferentLot.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteZone_WhenHasOccupiedSlots_Should_Return_BadRequest()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var lotId = await CreateTestLotAsync();

        var zoneResponse = await adminClient.PostAsync($"/api/lots/{lotId}/zones", new JsonContent(new CreateZoneModel
        {
            ParkingLotId = lotId,
            Code = "Z_OCC",
            Name = "Zone Occupied",
            Capacity = 10
        }));
        var zoneResult = await ResponseHelper.GetApiResultAsync<ZoneResponseModel>(zoneResponse);
        var zoneId = zoneResult.Result.Id;

        var pmsContext = _factory.GetRequiredService<PmsDbContext>();
        var vehicleType = await pmsContext.VehicleTypes.FirstOrDefaultAsync();
        if (vehicleType == null)
        {
            vehicleType = new VehicleType { Id = 1, Code = "CAR", Name = "Car", IsActive = true };
            pmsContext.VehicleTypes.Add(vehicleType);
            await pmsContext.SaveChangesAsync();
        }

        pmsContext.Slots.Add(new Slot
        {
            ZoneId = zoneId,
            VehicleTypeId = vehicleType.Id,
            Code = "S_OCC_1",
            Status = "Occupied",
            IsActive = true,
            RowVersion = Array.Empty<byte>()
        });
        await pmsContext.SaveChangesAsync();

        var deleteResponse = await adminClient.DeleteAsync($"/api/zones/{zoneId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Slot_UpdateStatus_WhenInvalid_Should_Return_BadRequest()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var lotId = await CreateTestLotAsync();

        var zoneResponse = await adminClient.PostAsync($"/api/lots/{lotId}/zones", new JsonContent(new CreateZoneModel
        {
            ParkingLotId = lotId,
            Code = "Z_SLOT",
            Name = "Zone For Slots",
            Capacity = 10
        }));
        var zoneResult = await ResponseHelper.GetApiResultAsync<ZoneResponseModel>(zoneResponse);

        var pmsContext = _factory.GetRequiredService<PmsDbContext>();
        var vehicleType = await pmsContext.VehicleTypes.FirstOrDefaultAsync();
        if (vehicleType == null)
        {
            vehicleType = new VehicleType { Id = 1, Code = "CAR", Name = "Car", IsActive = true };
            pmsContext.VehicleTypes.Add(vehicleType);
            await pmsContext.SaveChangesAsync();
        }

        var slotResponse = await adminClient.PostAsync($"/api/zones/{zoneResult.Result.Id}/slots", new JsonContent(new CreateSlotModel
        {
            ZoneId = zoneResult.Result.Id,
            VehicleTypeId = vehicleType.Id,
            Code = "SLOT_X",
            Status = "Available"
        }));
        var slotResult = await ResponseHelper.GetApiResultAsync<SlotResponseModel>(slotResponse);

        // Try updating to invalid status
        var badStatusResponse = await adminClient.PatchAsync($"/api/slots/{slotResult.Result.Id}/status", new JsonContent(new UpdateSlotStatusModel
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
            Capacity = 100
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

    [Fact]
    public async Task CreateZone_Without_Code_And_Type_Alias_Should_AutoGenerate_Code_And_Succeed()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var lotId = await CreateTestLotAsync();

        var response = await adminClient.PostAsync($"/api/parking-lots/{lotId}/zones", new JsonContent(new CreateZoneModel
        {
            ParkingLotId = lotId,
            Name = "Auto Code Zone",
            Type = "Car"
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ResponseHelper.GetApiResultAsync<ZoneResponseModel>(response);
        result.Succeeded.Should().BeTrue();
        result.Result.Code.Should().StartWith("ZN-");
        result.Result.ZoneType.Should().Be("Car");
    }

    [Fact]
    public async Task DeleteSlot_When_Reserved_Should_Return_BadRequest()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var lotId = await CreateTestLotAsync();

        var zoneResponse = await adminClient.PostAsync($"/api/lots/{lotId}/zones", new JsonContent(new CreateZoneModel
        {
            ParkingLotId = lotId,
            Code = "Z_RESV",
            Name = "Zone Reserved Test",
            Capacity = 10
        }));
        var zoneResult = await ResponseHelper.GetApiResultAsync<ZoneResponseModel>(zoneResponse);

        var pmsContext = _factory.GetRequiredService<PmsDbContext>();
        var vehicleType = await pmsContext.VehicleTypes.FirstOrDefaultAsync();
        if (vehicleType == null)
        {
            vehicleType = new VehicleType { Id = 1, Code = "CAR", Name = "Car", IsActive = true };
            pmsContext.VehicleTypes.Add(vehicleType);
            await pmsContext.SaveChangesAsync();
        }

        var slot = new Slot
        {
            ZoneId = zoneResult.Result.Id,
            VehicleTypeId = vehicleType.Id,
            Code = "SLOT_RES1",
            Status = "Reserved",
            IsActive = true,
            RowVersion = Array.Empty<byte>()
        };
        pmsContext.Slots.Add(slot);
        await pmsContext.SaveChangesAsync();

        var deleteResponse = await adminClient.DeleteAsync($"/api/slots/{slot.Id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await deleteResponse.Content.ReadAsStringAsync();
        content.Should().Contain("reserved");
    }

    [Fact]
    public async Task DeleteZone_When_Has_Reserved_Slots_Should_Return_BadRequest()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var lotId = await CreateTestLotAsync();

        var zoneResponse = await adminClient.PostAsync($"/api/lots/{lotId}/zones", new JsonContent(new CreateZoneModel
        {
            ParkingLotId = lotId,
            Code = "Z_ZNRES",
            Name = "Zone with Reserved Slot",
            Capacity = 10
        }));
        var zoneResult = await ResponseHelper.GetApiResultAsync<ZoneResponseModel>(zoneResponse);

        var pmsContext = _factory.GetRequiredService<PmsDbContext>();
        var vehicleType = await pmsContext.VehicleTypes.FirstOrDefaultAsync();
        if (vehicleType == null)
        {
            vehicleType = new VehicleType { Id = 1, Code = "CAR", Name = "Car", IsActive = true };
            pmsContext.VehicleTypes.Add(vehicleType);
            await pmsContext.SaveChangesAsync();
        }

        pmsContext.Slots.Add(new Slot
        {
            ZoneId = zoneResult.Result.Id,
            VehicleTypeId = vehicleType.Id,
            Code = "SLOT_RES2",
            Status = "Reserved",
            IsActive = true,
            RowVersion = Array.Empty<byte>()
        });
        await pmsContext.SaveChangesAsync();

        var deleteResponse = await adminClient.DeleteAsync($"/api/zones/{zoneResult.Result.Id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await deleteResponse.Content.ReadAsStringAsync();
        content.Should().Contain("reserved");
    }
}
