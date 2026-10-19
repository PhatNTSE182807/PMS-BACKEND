using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using PMS.API;
using PMS.Api.IntegrationTests.Common;
using PMS.Api.IntegrationTests.Helpers;
using PMS.Application.Models.Layout;
using PMS.Application.Models.ParkingLot;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

/// <summary>Public 3D layout channel and its 2D fallback (SRS FR-3D-01/02/06/08).</summary>
public class LayoutsEndpointTests
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    private async Task<int> CreateLotAsync(string template)
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var response = await adminClient.PostAsync("/api/lots", new JsonContent(new CreateParkingLotModel
        {
            Code = $"LY_{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            Name = "Layout Lot",
            Address = "9 Layout Rd",
            LayoutTemplate = template
        }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await ResponseHelper.GetApiResultAsync<ParkingLotResponseModel>(response)).Result.Id;
    }

    [Fact]
    public async Task Templates_Should_Be_Listed_For_Anyone()
    {
        var response = await _factory.CreateUnauthorizedClient().GetAsync("/api/lot-templates");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var templates = (await ResponseHelper.GetApiResultAsync<List<LotTemplateResponseModel>>(response)).Result;
        templates.Select(t => t.Id).Should().Equal("L3S20", "L3S50", "L4S30");
    }

    [Fact]
    public async Task Published_Layout_Status_And_2D_Should_Be_Public_And_Without_Plates()
    {
        var lotId = await CreateLotAsync("L4S30");
        var client = _factory.CreateUnauthorizedClient();

        var layoutResponse = await client.GetAsync($"/api/public/lots/{lotId}/layout");
        layoutResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var layout = (await ResponseHelper.GetApiResultAsync<LayoutResponseModel>(layoutResponse)).Result;
        layout.TemplateId.Should().Be("L4S30");
        layout.Floors.Should().HaveCount(4);
        layout.Zones.Should().HaveCount(4);
        layout.Slots.Should().HaveCount(120);

        var statusResponse = await client.GetAsync($"/api/public/lots/{lotId}/layout/status");
        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = (await ResponseHelper.GetApiResultAsync<LayoutStatusResponseModel>(statusResponse)).Result;
        status.Slots.Should().HaveCount(120).And.OnlyContain(s => s.Status == "Available");
        status.Zones.Should().OnlyContain(z => z.Free == 30 && z.Total == 30);

        var plan = (await ResponseHelper.GetApiResultAsync<Layout2DResponseModel>(
            await client.GetAsync($"/api/public/lots/{lotId}/layout/2d"))).Result;
        plan.Detail.Should().Be("slots");
        plan.Floors.Sum(f => f.Cells.Count).Should().Be(120);

        var lowDetail = (await ResponseHelper.GetApiResultAsync<Layout2DResponseModel>(
            await client.GetAsync($"/api/public/lots/{lotId}/layout/2d?detail=zones"))).Result;
        lowDetail.Floors.Should().OnlyContain(f => f.Cells.Count == 0 && f.ZoneBlocks.Count == 1);

        foreach (var raw in new[] { layoutResponse, statusResponse })
        {
            var json = (await raw.Content.ReadAsStringAsync()).ToLowerInvariant();
            json.Should().NotContain("platenumber").And.NotContain("licenseplate").And.NotContain("platenormalized");
        }
    }

    [Fact]
    public async Task Layout_Of_An_Unknown_Lot_Should_Return_NotFound()
    {
        var response = await _factory.CreateUnauthorizedClient().GetAsync("/api/public/lots/999999/layout");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
