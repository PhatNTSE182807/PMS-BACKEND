using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
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

public class SlotsEndpointTests
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    private async Task<(int zoneId, int vehicleTypeId)> SetupTestZoneAndVehicleTypeAsync()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var pmsContext = _factory.GetRequiredService<PmsDbContext>();

        var vehicleType = await pmsContext.VehicleTypes.FindAsync(1);
        if (vehicleType == null)
        {
            vehicleType = new VehicleType { Id = 1, Code = "CAR_4", Name = "4-Seat Car", IsActive = true };
            pmsContext.VehicleTypes.Add(vehicleType);
            await pmsContext.SaveChangesAsync();
        }

        var lotResponse = await adminClient.PostAsync("/api/lots", new JsonContent(new CreateParkingLotModel
        {
            Code = $"L_{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Lot for Slot Tests",
            Address = "456 Slot Ave",
            Capacity = 100
        }));
        var lotResult = await ResponseHelper.GetApiResultAsync<ParkingLotResponseModel>(lotResponse);

        var zoneResponse = await adminClient.PostAsync($"/api/lots/{lotResult.Result.Id}/zones", new JsonContent(new CreateZoneModel
        {
            ParkingLotId = lotResult.Result.Id,
            Code = $"Z_{Guid.NewGuid():N}"[..6].ToUpperInvariant(),
            Name = "Slot Zone",
            Capacity = 20
        }));
        var zoneResult = await ResponseHelper.GetApiResultAsync<ZoneResponseModel>(zoneResponse);

        return (zoneResult.Result.Id, vehicleType.Id);
    }

    [Fact]
    public async Task CreateSlot_WhenZoneNotFound_Should_Return_NotFound()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var model = new CreateSlotModel
        {
            ZoneId = 99999,
            VehicleTypeId = 1,
            Code = "S_ORPHAN",
            Status = "Available"
        };

        var response = await adminClient.PostAsync("/api/zones/99999/slots", new JsonContent(model));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Full_Slot_Lifecycle_Crud_Should_Work()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var (zoneId, vehicleTypeId) = await SetupTestZoneAndVehicleTypeAsync();

        // 1. Create Slot
        var slotCode = $"S_{Guid.NewGuid():N}"[..6].ToUpperInvariant();
        var createResponse = await adminClient.PostAsync($"/api/zones/{zoneId}/slots", new JsonContent(new CreateSlotModel
        {
            ZoneId = zoneId,
            VehicleTypeId = vehicleTypeId,
            Code = slotCode,
            Status = "Available",
            PosX = 5.0m,
            PosY = 10.0m
        }));
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var createResult = await ResponseHelper.GetApiResultAsync<SlotResponseModel>(createResponse);
        var slotId = createResult.Result.Id;
        createResult.Result.Code.Should().Be(slotCode);
        createResult.Result.Status.Should().Be("Available");

        // 2. Get Slots in Zone
        var listResponse = await adminClient.GetAsync($"/api/zones/{zoneId}/slots");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var listResult = await ResponseHelper.GetApiResultAsync<IEnumerable<SlotResponseModel>>(listResponse);
        listResult.Result.Should().Contain(s => s.Id == slotId);

        // 3. Get Slot by ID
        var getResponse = await adminClient.GetAsync($"/api/slots/{slotId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var getResult = await ResponseHelper.GetApiResultAsync<SlotResponseModel>(getResponse);
        getResult.Result.Code.Should().Be(slotCode);

        // 4. Update Slot Status (PATCH) to Occupied
        var patchResponse = await adminClient.PatchAsync($"/api/slots/{slotId}/status", new JsonContent(new UpdateSlotStatusModel
        {
            Status = "Occupied"
        }));
        patchResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 5. Try to delete occupied slot -> should return BadRequest
        var deleteOccupied = await adminClient.DeleteAsync($"/api/slots/{slotId}");
        deleteOccupied.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 6. Set status back to Available
        var makeAvailable = await adminClient.PatchAsync($"/api/slots/{slotId}/status", new JsonContent(new UpdateSlotStatusModel
        {
            Status = "Available"
        }));
        makeAvailable.StatusCode.Should().Be(HttpStatusCode.OK);

        // 7. Delete (Deactivate) Available slot
        var deleteResponse = await adminClient.DeleteAsync($"/api/slots/{slotId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify deactivated
        var verifyResponse = await adminClient.GetAsync($"/api/slots/{slotId}");
        var verifyResult = await ResponseHelper.GetApiResultAsync<SlotResponseModel>(verifyResponse);
        verifyResult.Result.IsActive.Should().BeFalse();
    }
}
