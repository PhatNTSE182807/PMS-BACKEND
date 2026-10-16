using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PMS.API;
using PMS.Api.IntegrationTests.Common;
using PMS.Api.IntegrationTests.Common.Constants;
using PMS.Api.IntegrationTests.Helpers;
using PMS.Api.IntegrationTests.Helpers.Services;
using PMS.Application.Models.User;
using PMS.Application.Services;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

public class LoginOtpEndpointTests
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    private EmailTestService EmailService => (EmailTestService)_factory.Services.GetRequiredService<IEmailService>();

    private async Task<HttpClient> CreateAnonymousClientAsync()
    {
        // Seeds the default user; the auth header is dropped so the OTP endpoints are called anonymously
        var client = await _factory.CreateDefaultClientAsync();
        client.DefaultRequestHeaders.Authorization = null;
        return client;
    }

    private async Task<string> RequestCodeAsync(HttpClient client)
    {
        var request = new RequestLoginOtpModel { Email = UserConstants.DefaultUserDb.Email };
        var apiResponse = await client.PostAsync("/api/users/login-otp/request", new JsonContent(request));
        apiResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var email = EmailService.SentEmails.Last(e => e.ToAddress == UserConstants.DefaultUserDb.Email);
        return Regex.Match(email.Body, @"\b\d{6}\b").Value;
    }

    private static Task<HttpResponseMessage> VerifyAsync(HttpClient client, string code)
    {
        var model = new VerifyLoginOtpModel { Email = UserConstants.DefaultUserDb.Email, Code = code };
        return client.PostAsync("/api/users/login-otp/verify", new JsonContent(model));
    }

    [Fact]
    public async Task Request_Should_Send_Six_Digit_Code_To_Registered_Email()
    {
        // Arrange
        var client = await CreateAnonymousClientAsync();

        // Act
        var code = await RequestCodeAsync(client);

        // Assert
        code.Should().MatchRegex(@"^\d{6}$");
    }

    [Fact]
    public async Task Request_For_Unknown_Email_Should_Return_Ok_Without_Sending_Email()
    {
        // Arrange
        var client = await CreateAnonymousClientAsync();
        var request = new RequestLoginOtpModel { Email = "nobody@gmail.com" };

        // Act
        var apiResponse = await client.PostAsync("/api/users/login-otp/request", new JsonContent(request));

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        EmailService.SentEmails.Should().NotContain(e => e.ToAddress == "nobody@gmail.com");
    }

    [Fact]
    public async Task Verify_With_Correct_Code_Should_Return_Token()
    {
        // Arrange
        var client = await CreateAnonymousClientAsync();
        var code = await RequestCodeAsync(client);

        // Act
        var apiResponse = await VerifyAsync(client, code);

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await ResponseHelper.GetApiResultAsync<LoginResponseModel>(apiResponse);
        CheckResponse.Succeeded(response);
        response.Result.Email.Should().Be(UserConstants.DefaultUserDb.Email);
        response.Result.Token.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Verify_With_Wrong_Code_Should_Return_BadRequest()
    {
        // Arrange
        var client = await CreateAnonymousClientAsync();
        var code = await RequestCodeAsync(client);
        var wrongCode = code == "000000" ? "111111" : "000000";

        // Act
        var apiResponse = await VerifyAsync(client, wrongCode);

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var response = await ResponseHelper.GetApiResultAsync<string>(apiResponse);
        CheckResponse.Failure(response);
    }

    [Fact]
    public async Task Code_Should_Only_Work_Once()
    {
        // Arrange
        var client = await CreateAnonymousClientAsync();
        var code = await RequestCodeAsync(client);
        (await VerifyAsync(client, code)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Act
        var apiResponse = await VerifyAsync(client, code);

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Too_Many_Wrong_Codes_Should_Lock_Out_Even_A_Correct_Code()
    {
        // Arrange
        var client = await CreateAnonymousClientAsync();
        var code = await RequestCodeAsync(client);
        var wrongCode = code == "000000" ? "111111" : "000000";
        for (var i = 0; i < 5; i++)
            await VerifyAsync(client, wrongCode);

        // Act
        var apiResponse = await VerifyAsync(client, code);

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await apiResponse.Content.ReadAsStringAsync()).Should().Contain("Too many failed attempts");
    }
}
