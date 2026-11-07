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
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

public class LotsEndpointTests
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    [Fact]
    public async Task Post_Lot_WhenAdmin_Should_Return_Success()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var model = new CreateParkingLotModel
        {
            Code = $"LOT_{Guid.NewGuid():N}"[..10].ToUpperInvariant(),
            Name = "Integration Test Lot",
            Address = "123 Integration Way",
            City = "HCMC",
            LayoutTemplate = "L3S20",
            Is24Hours = true
        };

        var response = await adminClient.PostAsync("/api/lots", new JsonContent(model));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var apiResult = await ResponseHelper.GetApiResultAsync<ParkingLotResponseModel>(response);
        CheckResponse.Succeeded(apiResult);
        apiResult.Result.Code.Should().Be(model.Code);
        apiResult.Result.Name.Should().Be(model.Name);
        // Saved as Active, shown as Maintenance until the lot has a Manager and Staff
        apiResult.Result.StoredStatus.Should().Be("Active");
        apiResult.Result.Status.Should().Be("Maintenance");
        apiResult.Result.IsBookable.Should().BeFalse();
    }

    [Fact]
    public async Task Post_Lot_WhenNonAdmin_Should_Return_Forbidden()
    {
        var userClient = await _factory.CreateClientWithRoleAsync("User");
        var model = new CreateParkingLotModel
        {
            Code = "LOT_FORBID",
            Name = "Forbidden Lot",
            Address = "123 Way",
            LayoutTemplate = "L3S20",
        };

        var response = await userClient.PostAsync("/api/lots", new JsonContent(model));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Post_Lot_WhenAnonymous_Should_Return_Unauthorized()
    {
        var unauthClient = _factory.CreateUnauthorizedClient();
        var model = new CreateParkingLotModel
        {
            Code = "LOT_ANON",
            Name = "Anon Lot",
            Address = "123 Way",
            LayoutTemplate = "L3S20",
        };

        var response = await unauthClient.PostAsync("/api/lots", new JsonContent(model));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_Public_Lots_WhenAnonymous_Should_Return_Success()
    {
        var unauthClient = _factory.CreateUnauthorizedClient();

        var response = await unauthClient.GetAsync("/api/public/lots");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var apiResult = await ResponseHelper.GetApiResultAsync<IEnumerable<PublicParkingLotResponseModel>>(response);
        CheckResponse.Succeeded(apiResult);
    }

    [Fact]
    public async Task Full_Lot_Lifecycle_Crud_Should_Work()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();

        // 1. Create
        var code = $"LOT_{Guid.NewGuid():N}"[..10].ToUpperInvariant();
        var createResponse = await adminClient.PostAsync("/api/lots", new JsonContent(new CreateParkingLotModel
        {
            Code = code,
            Name = "Lifecycle Lot",
            Address = "789 Test Ave",
            LayoutTemplate = "L3S20",
            Is24Hours = true
        }));
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var createResult = await ResponseHelper.GetApiResultAsync<ParkingLotResponseModel>(createResponse);
        var lotId = createResult.Result.Id;

        // 2. Read by ID
        var getResponse = await adminClient.GetAsync($"/api/lots/{lotId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var getResult = await ResponseHelper.GetApiResultAsync<ParkingLotResponseModel>(getResponse);
        getResult.Result.Code.Should().Be(code);

        // 3. Update
        var updateResponse = await adminClient.PutAsync($"/api/lots/{lotId}", new JsonContent(new UpdateParkingLotModel
        {
            Name = "Updated Lifecycle Lot",
            Address = "789 Test Ave Updated",
            Is24Hours = true
        }));
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 4. Update Status (Patch)
        var patchResponse = await adminClient.PatchAsync($"/api/lots/{lotId}/status", new JsonContent(new UpdateParkingLotStatusModel
        {
            Status = "Maintenance",
            ClosureReason = "Emergency repairs",
            MaintenanceNotice = "Back tomorrow"
        }));
        patchResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 5. Delete (Soft delete): only a closed lot can be deleted
        var closeResponse = await adminClient.PatchAsync($"/api/lots/{lotId}/status", new JsonContent(new UpdateParkingLotStatusModel
        {
            Status = "Closed",
            ClosureReason = "Lot removed"
        }));
        closeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var deleteResponse = await adminClient.DeleteAsync($"/api/lots/{lotId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 6. Verify lot is now soft-deleted (GetById returns 404)
        var getAfterDelete = await adminClient.GetAsync($"/api/lots/{lotId}");
        getAfterDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
