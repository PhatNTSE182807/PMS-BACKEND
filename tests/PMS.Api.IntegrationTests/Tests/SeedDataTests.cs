using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using PMS.API;
using PMS.Api.IntegrationTests.Common;
using PMS.Api.IntegrationTests.Helpers;
using PMS.Application.Models.User;
using PMS.DataAccess.Persistence;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

public class SeedDataTests
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    [Fact]
    public async Task Seeded_System_Administrator_Should_Log_In_With_Admin_Role()
    {
        // Arrange
        var client = _factory.CreateClient();
        var loginUserModel = new LoginUserModel { Email = "admin@gmail.com", Password = "Admin123@" };

        // Act
        var apiResponse = await client.PostAsync("/api/users/authenticate", new JsonContent(loginUserModel));

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await ResponseHelper.GetApiResultAsync<LoginResponseModel>(apiResponse);
        CheckResponse.Succeeded(response);
        response.Result.Role.Should().Be("Admin");
        response.Result.PhoneNumber.Should().Be("0977888888");
    }

    [Fact]
    public void Seeded_System_Administrator_Should_Have_A_Profile_With_Full_Name()
    {
        // Arrange
        _factory.CreateClient();
        var context = _factory.GetRequiredService<PmsDbContext>();

        // Act
        var profile = context.Customers.SingleOrDefault(c => c.Email == "admin@gmail.com");

        // Assert
        profile.Should().NotBeNull();
        profile!.FullName.Should().Be("System administrator");
        profile.Phone.Should().Be("0977888888");
    }
}
