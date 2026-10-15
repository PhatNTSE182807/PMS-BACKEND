using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using PMS.API;
using PMS.Api.IntegrationTests.Common;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

public class SwaggerDocumentTests
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    private async Task<JObject> GetSwaggerAsync()
    {
        var client = _factory.CreateClient();
        return JObject.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
    }

    [Fact]
    public async Task User_Endpoints_Should_Be_Listed_In_Three_Ordered_Groups()
    {
        // Act
        var swagger = await GetSwaggerAsync();

        // Assert
        swagger["tags"]!.Select(t => (string)t["name"]).Take(3).Should().Equal(
            "User Authentication", "User Profile Management", "User Management");
    }

    [Theory]
    [InlineData("/api/Users/authenticate", "post", "User Authentication")]
    [InlineData("/api/Users/login-otp/verify", "post", "User Authentication")]
    [InlineData("/api/Users/profile/vehicles", "post", "User Profile Management")]
    [InlineData("/api/Users/profile/change-password", "put", "User Profile Management")]
    [InlineData("/api/Users/{id}/profile", "get", "User Management")]
    public async Task Endpoint_Should_Belong_To_Its_Group(string path, string method, string tag)
    {
        // Act
        var swagger = await GetSwaggerAsync();

        // Assert
        swagger["paths"]![path]![method]!["tags"]!.Select(t => (string)t).Should().Equal(tag);
    }
}
