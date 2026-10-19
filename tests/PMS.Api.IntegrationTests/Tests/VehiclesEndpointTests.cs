using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PMS.API;
using PMS.Api.IntegrationTests.Common;
using PMS.Api.IntegrationTests.Helpers;
using PMS.Application.Models.Profile;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

public class VehiclesEndpointTests
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    private async Task<int> SeedVehicleTypeAsync()
    {
        var context = _factory.GetRequiredService<PmsDbContext>();
        var vehicleType = await context.VehicleTypes.FirstOrDefaultAsync(vt => vt.Code == "CAR_4");

        if (vehicleType == null)
        {
            vehicleType = new VehicleType { Code = "CAR_4", Name = "4-Seat Car", IsActive = true };
            context.VehicleTypes.Add(vehicleType);
            await context.SaveChangesAsync();
        }

        return vehicleType.Id;
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("00A-12345")]
    [InlineData("29-12345")]
    public async Task AddVehicle_Should_Return_BadRequest_And_Not_Save_When_Plate_Is_Invalid(string plate)
    {
        // Arrange
        var client = await _factory.CreateDefaultClientAsync();
        var vehicleTypeId = await SeedVehicleTypeAsync();
        var model = new CreateVehicleModel { LicensePlate = plate, VehicleTypeId = vehicleTypeId };

        // Act
        var apiResponse = await client.PostAsync("/api/users/profile/vehicles", new JsonContent(model));

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await apiResponse.Content.ReadAsStringAsync()).Should().Contain("not a valid Vietnamese plate");

        var context = _factory.GetRequiredService<PmsDbContext>();
        context.Vehicles.Any(v => v.PlateNumber == plate.ToUpperInvariant()).Should().BeFalse();
    }

    [Fact]
    public async Task AddVehicle_Should_Save_Vehicle_When_Plate_Is_Valid()
    {
        // Arrange
        var client = await _factory.CreateDefaultClientAsync();
        var vehicleTypeId = await SeedVehicleTypeAsync();
        var model = new CreateVehicleModel { LicensePlate = "29-B1 123.45", VehicleTypeId = vehicleTypeId, CitizenId = "079123456789" };

        // Act
        var apiResponse = await client.PostAsync("/api/users/profile/vehicles", new JsonContent(model));

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await ResponseHelper.GetApiResultAsync<VehicleResponseModel>(apiResponse);
        CheckResponse.Succeeded(response);
        response.Result.LicensePlate.Should().Be("29-B1 123.45");
        response.Result.CitizenId.Should().Be("079123456789");
    }
}
