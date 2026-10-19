using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using PMS.API;
using PMS.API.Hubs;
using PMS.Api.IntegrationTests.Common;
using PMS.Api.IntegrationTests.Helpers;
using PMS.Application.Models.ParkingLot;
using PMS.Application.Models.Slot;
using PMS.DataAccess.Persistence;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

/// <summary>Public real-time channel of the 3D model (SRS FR-3D-03/08).</summary>
public class LotHubEndpointTests
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    private record SlotStatusChanged(int LotId, int SlotId, string Status);

    [Fact]
    public async Task Lot_Hub_Should_Accept_Anonymous_Connections()
    {
        var response = await _factory.CreateUnauthorizedClient()
            .PostAsync($"{LotHub.Path}/negotiate?negotiateVersion=1", new StringContent(""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("connectionToken");
    }

    [Fact]
    public async Task Slot_Status_Change_Should_Be_Pushed_To_The_Lots_Group_Only()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var lot = (await ResponseHelper.GetApiResultAsync<ParkingLotResponseModel>(await adminClient.PostAsync("/api/lots", new JsonContent(new CreateParkingLotModel
        {
            Code = $"HB_{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Hub Lot",
            Address = "1 Hub Rd",
            LayoutTemplate = "L3S20",
        })))).Result;
        var slotId = await _factory.GetRequiredService<PmsDbContext>().Slots
            .Where(s => s.Zone.ParkingLotId == lot.Id)
            .Select(s => s.Id)
            .FirstAsync();

        var received = new TaskCompletionSource<SlotStatusChanged>(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherLot = new List<SlotStatusChanged>();
        await using var watcher = Connect();
        await using var other = Connect();
        watcher.On<SlotStatusChanged>("SlotStatusChanged", e => received.TrySetResult(e));
        other.On<SlotStatusChanged>("SlotStatusChanged", otherLot.Add);
        await watcher.StartAsync();
        await other.StartAsync();
        await watcher.InvokeAsync("JoinLot", lot.Id);
        await other.InvokeAsync("JoinLot", lot.Id + 1000);

        var patch = await adminClient.PatchAsync($"/api/slots/{slotId}/status", new JsonContent(new UpdateSlotStatusModel { Status = "Maintenance" }));
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        var pushed = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        pushed.Should().Be(new SlotStatusChanged(lot.Id, slotId, "Maintenance"));
        otherLot.Should().BeEmpty();
    }

    private HubConnection Connect() =>
        new HubConnectionBuilder()
            .WithUrl($"http://localhost{LotHub.Path}", options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .Build();
}
