using System;
using System.Collections.Generic;
using System.Linq;
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

/// <summary>Zones come from the lot's ready-made model; Admins can only adjust them.</summary>
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
            LayoutTemplate = "L3S20",
        }));
        var lotResult = await ResponseHelper.GetApiResultAsync<ParkingLotResponseModel>(lotResponse);
        return lotResult.Result.Id;
    }

    [Fact]
    public async Task Lot_Zones_Should_Be_One_Per_Level_Of_The_Model()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var lotId = await CreateTestLotAsync();

        var response = await adminClient.GetAsync($"/api/lots/{lotId}/zones");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var zones = (await ResponseHelper.GetApiResultAsync<IEnumerable<ZoneResponseModel>>(response)).Result
            .OrderBy(z => z.FloorNo).ToList();
        zones.Select(z => z.Code).Should().Equal("L1", "L2", "L3");
        zones.Should().OnlyContain(z => z.ZoneType == "Floor" && z.Capacity == 20 && z.TotalSlots == 20);
    }

    [Fact]
    public async Task Admin_Zone_Update_Should_Validate_And_Sync_The_Counter()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var lotId = await CreateTestLotAsync();
        var pmsContext = _factory.GetRequiredService<PmsDbContext>();
        var zoneId = await pmsContext.Zones.Where(z => z.ParkingLotId == lotId && z.Code == "L2").Select(z => z.Id).FirstAsync();

        // Reject MaxReservationSlots > Capacity
        var invalidUpdateResponse = await adminClient.PutAsync($"/api/zones/{zoneId}", new JsonContent(new UpdateZoneModel
        {
            Name = "Over Quota Level",
            Capacity = 20,
            MaxReservationSlots = 30
        }));
        invalidUpdateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // A valid update keeps the counter in step
        var validUpdateResponse = await adminClient.PutAsync($"/api/zones/{zoneId}", new JsonContent(new UpdateZoneModel
        {
            Name = "Level 2 · Cars (reserved share)",
            Capacity = 20,
            MaxReservationSlots = 12
        }));
        validUpdateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var updateResult = await ResponseHelper.GetApiResultAsync<ZoneResponseModel>(validUpdateResponse);
        updateResult.Result.MaxReservationSlots.Should().Be(12);

        var counter = await pmsContext.ZoneCounters.AsNoTracking().FirstAsync(zc => zc.ZoneId == zoneId);
        counter.TotalCapacity.Should().Be(20);
    }

    [Fact]
    public async Task Zone_Of_An_Unknown_Lot_Should_Return_Empty_Or_NotFound()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();

        var response = await adminClient.GetAsync("/api/zones/99999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
