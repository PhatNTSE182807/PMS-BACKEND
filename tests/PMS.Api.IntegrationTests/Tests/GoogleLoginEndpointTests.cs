using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using PMS.API;
using PMS.Api.IntegrationTests.Common;
using PMS.Api.IntegrationTests.Common.Constants;
using PMS.Api.IntegrationTests.Helpers;
using PMS.Api.IntegrationTests.Helpers.Services;
using PMS.Application.Models.User;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

public class GoogleLoginEndpointTests
{
    private const string GoogleEmail = "google.customer@gmail.com";

    private readonly ApiApplicationFactory<Program> _factory = new();

    private async Task<HttpClient> CreateAnonymousClientAsync()
    {
        // Seeds the default (Admin) user; the auth header is dropped so the endpoint is called anonymously
        var client = await _factory.CreateDefaultClientAsync();
        client.DefaultRequestHeaders.Authorization = null;
        return client;
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string idToken) =>
        client.PostAsync("/api/users/login-google", new JsonContent(new GoogleLoginModel { IdToken = idToken }));

    [Fact]
    public async Task First_Google_Sign_In_Should_Create_Customer_And_Return_Token()
    {
        // Arrange
        var client = await CreateAnonymousClientAsync();

        // Act
        var apiResponse = await LoginAsync(client, GoogleTokenTestValidator.Token(GoogleEmail, name: "Google Customer"));

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await ResponseHelper.GetApiResultAsync<LoginResponseModel>(apiResponse);
        CheckResponse.Succeeded(response);
        response.Result.Email.Should().Be(GoogleEmail);
        response.Result.Role.Should().Be("User");
        response.Result.Token.Should().NotBeNullOrWhiteSpace();

        using var scope = _factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(GoogleEmail);
        user!.EmailConfirmed.Should().BeTrue();
        var customer = scope.ServiceProvider.GetRequiredService<PmsDbContext>().Customers.Single(c => c.UserId == user.Id);
        customer.FullName.Should().Be("Google Customer");
    }

    [Fact]
    public async Task Second_Google_Sign_In_Should_Reuse_The_Same_Account()
    {
        // Arrange
        var client = await CreateAnonymousClientAsync();
        var first = await ResponseHelper.GetApiResultAsync<LoginResponseModel>(
            await LoginAsync(client, GoogleTokenTestValidator.Token(GoogleEmail)));

        // Act
        var second = await ResponseHelper.GetApiResultAsync<LoginResponseModel>(
            await LoginAsync(client, GoogleTokenTestValidator.Token(GoogleEmail)));

        // Assert
        second.Result.UserId.Should().Be(first.Result.UserId);
    }

    [Fact]
    public async Task Unverified_Google_Email_Should_Return_BadRequest()
    {
        // Arrange
        var client = await CreateAnonymousClientAsync();

        // Act
        var apiResponse = await LoginAsync(client, GoogleTokenTestValidator.Token(GoogleEmail, verified: false));

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Invalid_Google_Token_Should_Return_BadRequest()
    {
        // Arrange
        var client = await CreateAnonymousClientAsync();

        // Act
        var apiResponse = await LoginAsync(client, "forged-token");

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Internal_Account_Should_Not_Sign_In_With_Google()
    {
        // Arrange: the default test user is an Admin
        var client = await CreateAnonymousClientAsync();

        // Act
        var apiResponse = await LoginAsync(client, GoogleTokenTestValidator.Token(UserConstants.DefaultUserDb.Email));

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await apiResponse.Content.ReadAsStringAsync()).Should().Contain("must sign in with their username and password");
    }
}
