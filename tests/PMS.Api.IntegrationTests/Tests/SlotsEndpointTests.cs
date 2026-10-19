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
using PMS.DataAccess.Persistence;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

/// <summary>Slots come from the lot's ready-made model; staff only change their operating status.</summary>
public class SlotsEndpointTests
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    /// <summary>A new L3S20 lot and the id of its ground-level zone.</summary>
    private async Task<int> SetupZoneAsync()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var lotResponse = await adminClient.PostAsync("/api/lots", new JsonContent(new CreateParkingLotModel
        {
            Code = $"LS_{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Lot for Slot Tests",
            Address = "456 Slot Ave",
            LayoutTemplate = "L3S20",
        }));
        var lotResult = await ResponseHelper.GetApiResultAsync<ParkingLotResponseModel>(lotResponse);

        var pmsContext = _factory.GetRequiredService<PmsDbContext>();
        return await pmsContext.Zones
            .Where(z => z.ParkingLotId == lotResult.Result.Id && z.FloorNo == 0)
            .Select(z => z.Id)
            .FirstAsync();
    }

    [Fact]
    public async Task Slots_Should_Be_Generated_For_Each_Zone()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var zoneId = await SetupZoneAsync();

        var listResponse = await adminClient.GetAsync($"/api/zones/{zoneId}/slots");

        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var slots = (await ResponseHelper.GetApiResultAsync<IEnumerable<SlotResponseModel>>(listResponse)).Result.ToList();
        slots.Should().HaveCount(20);
        slots.Should().OnlyContain(s => s.Status == "Available" && s.Code.StartsWith("L1-"));
    }

    [Fact]
    public async Task Slot_Status_Should_Move_Between_Available_And_Maintenance()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var zoneId = await SetupZoneAsync();
        var slots = (await ResponseHelper.GetApiResultAsync<IEnumerable<SlotResponseModel>>(
            await adminClient.GetAsync($"/api/zones/{zoneId}/slots"))).Result;
        var slotId = slots.First().Id;

        var maintenance = await adminClient.PatchAsync($"/api/slots/{slotId}/status", new JsonContent(new UpdateSlotStatusModel { Status = "Maintenance" }));
        maintenance.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResponse = await adminClient.GetAsync($"/api/slots/{slotId}");
        (await ResponseHelper.GetApiResultAsync<SlotResponseModel>(getResponse)).Result.Status.Should().Be("Maintenance");

        var available = await adminClient.PatchAsync($"/api/slots/{slotId}/status", new JsonContent(new UpdateSlotStatusModel { Status = "Available" }));
        available.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
