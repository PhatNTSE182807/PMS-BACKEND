using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PMS.API;
using PMS.Api.IntegrationTests.Common;
using PMS.Api.IntegrationTests.Helpers;
using PMS.Application.Models.Admin;
using PMS.Application.Models.Profile;
using PMS.Application.Models.User;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

public class TwoFactorAuthIntegrationTests
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    /// <summary>Admin-created accounts start without 2FA; these tests cover accounts that have it on.</summary>
    private async Task EnableTwoFactorAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(userId);
        await userManager.SetTwoFactorEnabledAsync(user!, true);
    }

    [Fact]
    public async Task Password_Login_For_Staff_With_2FA_Should_Return_2FA_Required_And_No_JWT()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var unauthClient = _factory.CreateUnauthorizedClient();

        var username = $"staff_2fa_{Guid.NewGuid():N}"[..12];
        var email = $"{username}@test.com";

        // 1. Admin creates staff
        var createResponse = await _factory.CreateStaffThroughManagerAsync(new CreateEmployeeAccountModel
        {
            UserName = username,
            FullName = "Staff 2FA Test",
            Password = "ComplexPassword.1!",
            Email = email,
            PhoneNumber = "0901234567"
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var createResult = await ResponseHelper.GetApiResultAsync<CreateUserResponseModel>(createResponse);

        // 2. Staff has two-factor sign-in turned on
        await EnableTwoFactorAsync(createResult.Result.Id.ToString());

        // 3. Staff logs in with password
        var loginResponse = await unauthClient.PostAsync("/api/users/authenticate", new JsonContent(new LoginUserModel
        {
            Username = username,
            Password = "ComplexPassword.1!"
        }));
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var loginResult = await ResponseHelper.GetApiResultAsync<LoginResponseModel>(loginResponse);
        loginResult.Succeeded.Should().BeTrue();
        loginResult.Result.RequiresTwoFactor.Should().BeTrue();
        loginResult.Result.Token.Should().BeNull(); // No JWT at this stage
        loginResult.Result.TwoFactorToken.Should().NotBeNullOrEmpty();
        loginResult.Result.Destination.Should().NotBeNullOrEmpty();

        // Verify in DB that an OTP record was stored
        using var scope = _factory.Services.CreateScope();
        var pmsContext = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
        var otp = await pmsContext.OtpCodes.FirstOrDefaultAsync(o => o.Destination == email);
        otp.Should().NotBeNull();
        otp.Purpose.Should().Be("TwoFactorLogin");
        otp.ConsumedAt.Should().BeNull();
    }

    [Fact]
    public async Task Verify_TwoFactor_With_Correct_Code_Should_Return_JWT_And_Authorize_Subsequent_Requests()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var unauthClient = _factory.CreateUnauthorizedClient();

        var username = $"staff_ver_{Guid.NewGuid():N}"[..12];
        var email = $"{username}@test.com";

        // 1. Create staff with two-factor sign-in
        var createResponse = await _factory.CreateStaffThroughManagerAsync(new CreateEmployeeAccountModel
        {
            UserName = username,
            FullName = "Staff Verify Test",
            Password = "ComplexPassword.1!",
            Email = email,
            PhoneNumber = "0901234567"
        });
        var createResult = await ResponseHelper.GetApiResultAsync<CreateUserResponseModel>(createResponse);

        await EnableTwoFactorAsync(createResult.Result.Id.ToString());

        // 2. Initiate password login
        var loginResponse = await unauthClient.PostAsync("/api/users/authenticate", new JsonContent(new LoginUserModel
        {
            Username = username,
            Password = "ComplexPassword.1!"
        }));
        var loginResult = await ResponseHelper.GetApiResultAsync<LoginResponseModel>(loginResponse);

        // 3. Find the valid OTP code in DB
        using var scope = _factory.Services.CreateScope();
        var pmsContext = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
        var configuration = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        var secretKey = configuration.GetValue<string>("JwtConfiguration:SecretKey");
        var otpRecord = await pmsContext.OtpCodes.FirstAsync(o => o.Destination == email && o.ConsumedAt == null);

        // Find matching 6-digit code by testing range (in Dev/Integration environment)
        string matchingCode = null;
        for (int c = 100000; c <= 999999; c++)
        {
            var codeStr = c.ToString("D6");
            var hash = PMS.Application.Helpers.TwoFactorTokenHelper.HashOtpCode(codeStr, email, secretKey);
            if (hash == otpRecord.CodeHash)
            {
                matchingCode = codeStr;
                break;
            }
        }
        matchingCode.Should().NotBeNull();

        // 4. Verify OTP via POST /api/users/two-factor
        var verifyResponse = await unauthClient.PostAsync("/api/users/two-factor", new JsonContent(new TwoFactorLoginModel
        {
            TwoFactorToken = loginResult.Result.TwoFactorToken,
            Code = matchingCode
        }));
        verifyResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var verifyResult = await ResponseHelper.GetApiResultAsync<LoginResponseModel>(verifyResponse);
        verifyResult.Succeeded.Should().BeTrue();
        verifyResult.Result.RequiresTwoFactor.Should().BeFalse();
        verifyResult.Result.Token.Should().NotBeNullOrEmpty();
        verifyResult.Result.Username.Should().Be(username);

        // 5. Use the returned JWT token to access authorized endpoint
        var authStaffClient = _factory.CreateClient();
        authStaffClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", verifyResult.Result.Token);

        var profileResponse = await authStaffClient.GetAsync("/api/users/profile");
        profileResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var profileResult = await ResponseHelper.GetApiResultAsync<UserProfileResponseModel>(profileResponse);
        profileResult.Result.Email.Should().Be(email);
    }

    [Fact]
    public async Task Verify_TwoFactor_With_Wrong_Code_Should_Return_BadRequest()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var unauthClient = _factory.CreateUnauthorizedClient();

        var username = $"staff_fail_{Guid.NewGuid():N}"[..12];
        var email = $"{username}@test.com";

        var createResponse = await _factory.CreateStaffThroughManagerAsync(new CreateEmployeeAccountModel
        {
            UserName = username,
            FullName = "Staff Fail Test",
            Password = "ComplexPassword.1!",
            Email = email,
            PhoneNumber = "0901234567"
        });
        var createResult = await ResponseHelper.GetApiResultAsync<CreateUserResponseModel>(createResponse);

        await EnableTwoFactorAsync(createResult.Result.Id.ToString());

        var loginResponse = await unauthClient.PostAsync("/api/users/authenticate", new JsonContent(new LoginUserModel
        {
            Username = username,
            Password = "ComplexPassword.1!"
        }));
        var loginResult = await ResponseHelper.GetApiResultAsync<LoginResponseModel>(loginResponse);

        // Submit wrong OTP code
        var verifyResponse = await unauthClient.PostAsync("/api/users/two-factor", new JsonContent(new TwoFactorLoginModel
        {
            TwoFactorToken = loginResult.Result.TwoFactorToken,
            Code = "000000"
        }));

        verifyResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Resend_TwoFactor_Within_Cooldown_Should_Be_RateLimited()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var unauthClient = _factory.CreateUnauthorizedClient();

        var username = $"staff_rate_{Guid.NewGuid():N}"[..12];
        var email = $"{username}@test.com";

        var createResponse = await _factory.CreateStaffThroughManagerAsync(new CreateEmployeeAccountModel
        {
            UserName = username,
            FullName = "Staff Rate Test",
            Password = "ComplexPassword.1!",
            Email = email,
            PhoneNumber = "0901234567"
        });
        var createResult = await ResponseHelper.GetApiResultAsync<CreateUserResponseModel>(createResponse);

        await EnableTwoFactorAsync(createResult.Result.Id.ToString());

        var loginResponse = await unauthClient.PostAsync("/api/users/authenticate", new JsonContent(new LoginUserModel
        {
            Username = username,
            Password = "ComplexPassword.1!"
        }));
        var loginResult = await ResponseHelper.GetApiResultAsync<LoginResponseModel>(loginResponse);

        // Immediate resend attempt
        var resendResponse = await unauthClient.PostAsync("/api/users/two-factor/resend", new JsonContent(new ResendTwoFactorModel
        {
            TwoFactorToken = loginResult.Result.TwoFactorToken
        }));

        resendResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var resendContent = await resendResponse.Content.ReadAsStringAsync();
        resendContent.Should().Contain("seconds before requesting a new verification code");
    }

    [Fact]
    public async Task TwoFactorChallengeToken_Cannot_Be_Used_As_Bearer_Access_Token_On_Authorized_Endpoints()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var unauthClient = _factory.CreateUnauthorizedClient();

        var username = $"staff_bypass_{Guid.NewGuid():N}"[..12];
        var email = $"{username}@test.com";

        var createResponse = await _factory.CreateStaffThroughManagerAsync(new CreateEmployeeAccountModel
        {
            UserName = username,
            FullName = "Staff Bypass Test",
            Password = "ComplexPassword.1!",
            Email = email,
            PhoneNumber = "0901234567"
        });
        var createResult = await ResponseHelper.GetApiResultAsync<CreateUserResponseModel>(createResponse);

        await EnableTwoFactorAsync(createResult.Result.Id.ToString());

        var loginResponse = await unauthClient.PostAsync("/api/users/authenticate", new JsonContent(new LoginUserModel
        {
            Username = username,
            Password = "ComplexPassword.1!"
        }));
        var loginResult = await ResponseHelper.GetApiResultAsync<LoginResponseModel>(loginResponse);
        loginResult.Result.TwoFactorToken.Should().NotBeNullOrEmpty();

        // Attempt to access authorized endpoint using challenge token as Bearer token
        var bypassClient = _factory.CreateClient();
        bypassClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", loginResult.Result.TwoFactorToken);

        var profileResponse = await bypassClient.GetAsync("/api/users/profile");
        profileResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Internal_Accounts_Cannot_Log_In_Via_Passwordless_OTP_Without_Password()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var unauthClient = _factory.CreateUnauthorizedClient();

        var username = $"staff_nopwd_{Guid.NewGuid():N}"[..12];
        var email = $"{username}@test.com";

        var createResponse = await _factory.CreateStaffThroughManagerAsync(new CreateEmployeeAccountModel
        {
            UserName = username,
            FullName = "Staff No Pwd Test",
            Password = "ComplexPassword.1!",
            Email = email,
            PhoneNumber = "0901234567"
        });
        var createResult = await ResponseHelper.GetApiResultAsync<CreateUserResponseModel>(createResponse);

        await EnableTwoFactorAsync(createResult.Result.Id.ToString());

        // Attempt passwordless OTP verification for internal account
        var verifyResponse = await unauthClient.PostAsync("/api/users/two-factor", new JsonContent(new TwoFactorLoginModel
        {
            Email = email,
            Code = "123456"
        }));

        verifyResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await verifyResponse.Content.ReadAsStringAsync();
        content.Should().Contain("must authenticate with username and password first");
    }
}
