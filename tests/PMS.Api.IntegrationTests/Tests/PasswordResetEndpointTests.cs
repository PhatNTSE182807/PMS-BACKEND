using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
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
using PMS.Application.Services;
using PMS.DataAccess.Identity;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

/// <summary>Forgot / reset password by email link (FR-AUTH-12), with real Identity tokens.</summary>
public class PasswordResetEndpointTests
{
    private const string NewPassword = "NewPass!1";

    private readonly ApiApplicationFactory<Program> _factory = new();

    private EmailTestService EmailService => (EmailTestService)_factory.Services.GetRequiredService<IEmailService>();

    private async Task<HttpClient> CreateAnonymousClientAsync()
    {
        // Seeds the default user; the auth header is dropped so the endpoints are called anonymously
        var client = await _factory.CreateDefaultClientAsync();
        client.DefaultRequestHeaders.Authorization = null;
        return client;
    }

    private static Task<HttpResponseMessage> ForgotAsync(HttpClient client, string email) =>
        client.PostAsync("/api/users/forgot-password", new JsonContent(new ForgotPasswordModel { Email = email }));

    private static Task<HttpResponseMessage> ResetAsync(HttpClient client, string email, string token) =>
        client.PostAsync("/api/users/reset-password", new JsonContent(new ResetPasswordModel
        {
            Email = email,
            Token = token,
            NewPassword = NewPassword,
            ConfirmPassword = NewPassword
        }));

    /// <summary>Reads the token from the reset link in the last email sent to this address.</summary>
    private string TokenFromLastEmail(string email)
    {
        var message = EmailService.SentEmails.Last(e => e.ToAddress == email);
        return Regex.Match(message.Body, @"[?;&]token=([A-Za-z0-9_-]+)").Groups[1].Value;
    }

    private async Task<ApplicationUser> FindUserAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await userManager.FindByEmailAsync(email);
    }

    private async Task<bool> PasswordMatchesAsync(string email, string password)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email);
        return await userManager.CheckPasswordAsync(user!, password);
    }

    [Fact]
    public async Task Forgot_Should_Email_A_Reset_Link_To_The_Frontend()
    {
        // Arrange
        var client = await CreateAnonymousClientAsync();
        var email = UserConstants.DefaultUserDb.Email;

        // Act
        var apiResponse = await ForgotAsync(client, email);

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var message = EmailService.SentEmails.Last(e => e.ToAddress == email);
        message.Subject.Should().Be("Reset your password");
        message.Body.Should().Contain("http://localhost:5173/reset-password?email=");
        TokenFromLastEmail(email).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Forgot_For_Unknown_Email_Should_Return_The_Same_Answer_Without_Email()
    {
        // Arrange
        var client = await CreateAnonymousClientAsync();
        const string unknownEmail = "nobody.here@gmail.com";

        // Act
        var knownResponse = await ForgotAsync(client, UserConstants.DefaultUserDb.Email);
        var unknownResponse = await ForgotAsync(client, unknownEmail);

        // Assert
        unknownResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await unknownResponse.Content.ReadAsStringAsync())
            .Should().Be(await knownResponse.Content.ReadAsStringAsync());
        EmailService.SentEmails.Should().NotContain(e => e.ToAddress == unknownEmail);
        (await FindUserAsync(unknownEmail)).Should().BeNull();
    }

    [Fact]
    public async Task Reset_With_Link_Should_Change_The_Password_And_Send_Notice()
    {
        // Arrange
        var client = await CreateAnonymousClientAsync();
        var email = UserConstants.DefaultUserDb.Email;
        await ForgotAsync(client, email);
        var token = TokenFromLastEmail(email);

        // Act
        var apiResponse = await ResetAsync(client, email, token);

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PasswordMatchesAsync(email, NewPassword)).Should().BeTrue();
        (await PasswordMatchesAsync(email, UserConstants.DefaultUserDb.Password)).Should().BeFalse();
        EmailService.SentEmails.Last(e => e.ToAddress == email).Subject.Should().Be("Your password was changed");
    }

    [Fact]
    public async Task Link_Should_Only_Work_Once()
    {
        // Arrange
        var client = await CreateAnonymousClientAsync();
        var email = UserConstants.DefaultUserDb.Email;
        await ForgotAsync(client, email);
        var token = TokenFromLastEmail(email);
        (await ResetAsync(client, email, token)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Act
        var apiResponse = await ResetAsync(client, email, token);

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await apiResponse.Content.ReadAsStringAsync()).Should().Contain("invalid or has expired");
    }

    [Fact]
    public async Task Reset_With_Tampered_Token_Should_Return_BadRequest()
    {
        // Arrange
        var client = await CreateAnonymousClientAsync();
        var email = UserConstants.DefaultUserDb.Email;

        // Act
        var apiResponse = await ResetAsync(client, email, "not-a-real-token");

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PasswordMatchesAsync(email, UserConstants.DefaultUserDb.Password)).Should().BeTrue();
    }

    [Fact]
    public async Task Account_Without_Password_Should_Get_A_Set_Password_Link_That_Works()
    {
        // Arrange: a login-code request creates a password-less customer, like a Google sign-in does
        var client = await CreateAnonymousClientAsync();
        const string email = "google.only@gmail.com";
        await client.PostAsync("/api/users/login-otp/request", new JsonContent(new RequestLoginOtpModel { Email = email }));

        // Act
        await ForgotAsync(client, email);
        var message = EmailService.SentEmails.Last(e => e.ToAddress == email);
        var apiResponse = await ResetAsync(client, email, TokenFromLastEmail(email));

        // Assert
        message.Subject.Should().Be("Set a password for your account");
        message.Body.Should().Contain("Sign in with Google");
        apiResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PasswordMatchesAsync(email, NewPassword)).Should().BeTrue();
        (await FindUserAsync(email))!.EmailConfirmed.Should().BeTrue();
    }

    [Fact]
    public async Task Too_Many_Requests_Should_Be_Rejected()
    {
        // Arrange: 5 requests per 15 minutes per client
        var client = await CreateAnonymousClientAsync();
        for (var i = 0; i < 5; i++)
            (await ForgotAsync(client, "someone@gmail.com")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Act
        var apiResponse = await ForgotAsync(client, "someone@gmail.com");

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }
}
