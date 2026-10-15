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
using PMS.Application.Models.Zone;
using PMS.DataAccess.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

public class ZonesEndpointTests
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    private async Task<int> CreateTestLotAsync()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var lotResponse = await adminClient.PostAsync("/api/lots", new JsonContent(new CreateParkingLotModel
        {
            Code = $"L_{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Lot for Zone Tests",
            Address = "123 Zone St",
            Capacity = 100
        }));
        var lotResult = await ResponseHelper.GetApiResultAsync<ParkingLotResponseModel>(lotResponse);
        return lotResult.Result.Id;
    }

    [Fact]
    public async Task CreateZone_WhenParentLotNotFound_Should_Return_NotFound()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var model = new CreateZoneModel
        {
            ParkingLotId = 99999,
            Code = "Z_ORPHAN",
            Name = "Orphan Zone",
            Capacity = 20
        };

        var response = await adminClient.PostAsync("/api/lots/99999/zones", new JsonContent(model));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Full_Zone_Lifecycle_Crud_Should_Work()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var lotId = await CreateTestLotAsync();

        // 1. Create Zone
        var zoneCode = $"Z_{Guid.NewGuid():N}"[..6].ToUpperInvariant();
        var createResponse = await adminClient.PostAsync($"/api/lots/{lotId}/zones", new JsonContent(new CreateZoneModel
        {
            ParkingLotId = lotId,
            Code = zoneCode,
            Name = "Floor 1 Section A",
            ZoneType = "Floor",
            FloorNo = 1,
            Capacity = 30
        }));
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var createResult = await ResponseHelper.GetApiResultAsync<ZoneResponseModel>(createResponse);
        var zoneId = createResult.Result.Id;
        createResult.Result.Code.Should().Be(zoneCode);

        // 2. Get Zones for Lot
        var listResponse = await adminClient.GetAsync($"/api/lots/{lotId}/zones");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var listResult = await ResponseHelper.GetApiResultAsync<IEnumerable<ZoneResponseModel>>(listResponse);
        listResult.Result.Should().Contain(z => z.Id == zoneId);

        // 3. Get Zone by ID
        var getResponse = await adminClient.GetAsync($"/api/zones/{zoneId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var getResult = await ResponseHelper.GetApiResultAsync<ZoneResponseModel>(getResponse);
        getResult.Result.Code.Should().Be(zoneCode);

        // 4. Update Zone
        var updateResponse = await adminClient.PutAsync($"/api/zones/{zoneId}", new JsonContent(new UpdateZoneModel
        {
            Name = "Updated Section A",
            Capacity = 35
        }));
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 5. Delete Zone
        var deleteResponse = await adminClient.DeleteAsync($"/api/zones/{zoneId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify deactivated
        var verifyResponse = await adminClient.GetAsync($"/api/zones/{zoneId}");
        var verifyResult = await ResponseHelper.GetApiResultAsync<ZoneResponseModel>(verifyResponse);
        verifyResult.Result.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Zone_With_MaxReservationSlots_And_ZoneCounter_Lifecycle_Should_Work()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var lotId = await CreateTestLotAsync();

        // 1. Create Zone with MaxReservationSlots
        var zoneCode = $"ZR_{Guid.NewGuid():N}"[..6].ToUpperInvariant();
        var createResponse = await adminClient.PostAsync($"/api/lots/{lotId}/zones", new JsonContent(new CreateZoneModel
        {
            ParkingLotId = lotId,
            Code = zoneCode,
            Name = "Reservation Section",
            Capacity = 20,
            MaxReservationSlots = 8
        }));
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var createResult = await ResponseHelper.GetApiResultAsync<ZoneResponseModel>(createResponse);
        createResult.Result.MaxReservationSlots.Should().Be(8);
        var zoneId = createResult.Result.Id;

        // Verify ZoneCounter in DB
        var pmsContext = _factory.GetRequiredService<PmsDbContext>();
        var counter = await pmsContext.ZoneCounters.AsNoTracking().FirstOrDefaultAsync(zc => zc.ZoneId == zoneId);
        counter.Should().NotBeNull();
        counter.TotalCapacity.Should().Be(20);
        counter.AvailableSpaces.Should().Be(20);
        counter.OccupiedSpaces.Should().Be(0);

        // 2. Reject MaxReservationSlots > Capacity
        var invalidUpdateResponse = await adminClient.PutAsync($"/api/zones/{zoneId}", new JsonContent(new UpdateZoneModel
        {
            Name = "Over Quota Section",
            Capacity = 20,
            MaxReservationSlots = 30
        }));
        invalidUpdateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 3. Valid update synchronizes counter
        var validUpdateResponse = await adminClient.PutAsync($"/api/zones/{zoneId}", new JsonContent(new UpdateZoneModel
        {
            Name = "Updated Quota Section",
            Capacity = 25,
            MaxReservationSlots = 12
        }));
        validUpdateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var updateResult = await ResponseHelper.GetApiResultAsync<ZoneResponseModel>(validUpdateResponse);
        updateResult.Result.MaxReservationSlots.Should().Be(12);

        var updatedCounter = await pmsContext.ZoneCounters.AsNoTracking().FirstOrDefaultAsync(zc => zc.ZoneId == zoneId);
        updatedCounter.TotalCapacity.Should().Be(25);
        updatedCounter.AvailableSpaces.Should().Be(25);
    }
}
