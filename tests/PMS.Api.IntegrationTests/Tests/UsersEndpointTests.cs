using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PMS.API;
using PMS.Api.IntegrationTests.Common;
using PMS.Api.IntegrationTests.Common.Constants;
using PMS.Api.IntegrationTests.Helpers;
using PMS.Application.Models;
using PMS.Application.Models.User;
using PMS.DataAccess.Persistence;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

public class UsersEndpointTests
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    [Fact]
    public async Task Register_User_Should_Add_User_To_Database()
    {
        // Arrange
        var client = await _factory.CreateDefaultClientAsync();
        var context = _factory.GetRequiredService<DatabaseContext>();

        var createModel = Builder<CreateUserModel>.CreateNew()
            .With(cu => cu.Email = "IntegrationTest@gmail.com")
            .With(cu => cu.UserName = "IntegrationTest")
            .With(cu => cu.Password = "Password.1!")
            .With(cu => cu.PhoneNumber = "0987654321")
            .Build();

        // Act
        var apiResponse = await client.PostAsync("/api/users/register", new JsonContent(createModel));

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await ResponseHelper.GetApiResultAsync<CreateUserResponseModel>(apiResponse);
        CheckResponse.Succeeded(response);
        var userInDb = await context.Users.FirstOrDefaultAsync(u => u.Id == response.Result.Id.ToString());
        userInDb.Should().NotBeNull();
        userInDb.PhoneNumber.Should().Be("0987654321");
        userInDb.PasswordHash.Should().NotBeNullOrEmpty();
        userInDb.PasswordHash.Should().NotBe("Password.1!");
    }

    [Fact]
    public async Task Register_User_Should_Return_BadRequest_If_The_Email_Is_Incorrect()
    {
        // Arrange
        var client = await _factory.CreateDefaultClientAsync();
        var createModel = Builder<CreateUserModel>.CreateNew()
            .With(cu => cu.Email = "BadEmail")
            .With(cu => cu.PhoneNumber = "0987654321")
            .With(cu => cu.Password = "Password.1!")
            .Build();

        // Act
        var apiResponse = await client.PostAsync("/api/users/register", new JsonContent(createModel));

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var response = await ResponseHelper.GetApiResultAsync<string>(apiResponse);
        CheckResponse.Failure(response);
    }

    [Fact]
    public async Task Register_User_Should_Return_BadRequest_If_The_Username_Is_Incorrect()
    {
        // Arrange
        var client = await _factory.CreateDefaultClientAsync();
        var createModel = Builder<CreateUserModel>.CreateNew()
            .With(cu => cu.Email = "nuyonu@gmail.com")
            .With(cu => cu.UserName = "Len")
            .With(cu => cu.Password = "Password.1!")
            .With(cu => cu.PhoneNumber = "0987654321")
            .Build();

        // Act
        var apiResponse = await client.PostAsync("/api/users/register", new JsonContent(createModel));

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var response = await ResponseHelper.GetApiResultAsync<string>(apiResponse);
        CheckResponse.Failure(response);
    }

    [Fact]
    public async Task Register_User_Should_Return_BadRequest_If_The_Password_Is_Incorrect()
    {
        // Arrange
        var client = await _factory.CreateDefaultClientAsync();
        var createModel = Builder<CreateUserModel>.CreateNew()
            .With(cu => cu.Email = "nuyonu@gmail.com")
            .With(cu => cu.UserName = "ValidUser")
            .With(cu => cu.PhoneNumber = "0987654321")
            .With(cu => cu.Password = "incorrect")
            .Build();

        // Act
        var apiResponse = await client.PostAsync("/api/users/register", new JsonContent(createModel));

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var response = await ResponseHelper.GetApiResultAsync<string>(apiResponse);
        CheckResponse.Failure(response);
    }

    [Theory]
    [InlineData("0123456789")]
    [InlineData("123456789")]
    [InlineData("09876")]
    [InlineData("badphone")]
    public async Task Register_User_Should_Return_BadRequest_If_The_PhoneNumber_Is_Incorrect(string invalidPhone)
    {
        // Arrange
        var client = await _factory.CreateDefaultClientAsync();
        var createModel = Builder<CreateUserModel>.CreateNew()
            .With(cu => cu.Email = "validphone@gmail.com")
            .With(cu => cu.UserName = "ValidPhoneUser")
            .With(cu => cu.Password = "Password.1!")
            .With(cu => cu.PhoneNumber = invalidPhone)
            .Build();

        // Act
        var apiResponse = await client.PostAsync("/api/users/register", new JsonContent(createModel));

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var response = await ResponseHelper.GetApiResultAsync<string>(apiResponse);
        CheckResponse.Failure(response);
    }

    [Fact]
    public async Task Login_Should_Return_User_Information_And_Token()
    {
        // Arrange
        var client = await _factory.CreateDefaultClientAsync();
        var loginUserModel = Builder<LoginUserModel>.CreateNew()
            .With(cu => cu.Username = UserConstants.DefaultUserDb.Username)
            .With(cu => cu.Password = UserConstants.DefaultUserDb.Password)
            .Build();

        // Act
        var apiResponse = await client.PostAsync("/api/users/authenticate", new JsonContent(loginUserModel));

        // Assert
        var response = await ResponseHelper.GetApiResultAsync<LoginResponseModel>(apiResponse);
        CheckResponse.Succeeded(response);
        response.Result.Username.Should().Be(UserConstants.DefaultUserDb.Username);
        response.Result.Email.Should().Be(UserConstants.DefaultUserDb.Email);
        response.Result.Token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_Should_Return_NotFoundException_If_Username_Does_Not_Exists_In_Database()
    {
        // Arrange
        var client = await _factory.CreateDefaultClientAsync();
        var loginUserModel = Builder<LoginUserModel>.CreateNew()
            .With(cu => cu.Username = "NotExist")
            .With(cu => cu.Password = "Password.1!")
            .Build();

        // Act
        var apiResponse = await client.PostAsync("/api/users/authenticate", new JsonContent(loginUserModel));

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var response = await ResponseHelper.GetApiResultAsync<string>(apiResponse);
        CheckResponse.Failure(response);
    }

    [Fact]
    public async Task Login_Should_Return_BadRequest_If_Password_Is_Incorrect()
    {
        // Arrange
        var client = await _factory.CreateDefaultClientAsync();
        var loginUserModel = Builder<LoginUserModel>.CreateNew()
            .With(cu => cu.Username = UserConstants.DefaultUserDb.Username)
            .With(cu => cu.Password = "Password.1")
            .Build();

        // Act
        var apiResponse = await client.PostAsync("/api/users/authenticate", new JsonContent(loginUserModel));

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var response = await ResponseHelper.GetApiResultAsync<string>(apiResponse);
        CheckResponse.Failure(response);
    }

    [Fact]
    public async Task GetAll_Users_Should_Return_Registered_Accounts()
    {
        // Arrange
        var client = await _factory.CreateDefaultClientAsync();

        // Act
        var apiResponse = await client.GetAsync("/api/users");

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await ResponseHelper.GetApiResultAsync<List<UserResponseModel>>(apiResponse);
        CheckResponse.Succeeded(response);
        response.Result.Should().NotBeNull();
        response.Result.Should().Contain(u => u.UserName == UserConstants.DefaultUserDb.Username);
    }

    [Fact]
    public async Task GetById_User_Should_Return_User_Information()
    {
        // Arrange
        var client = await _factory.CreateDefaultClientAsync();
        var context = _factory.GetRequiredService<DatabaseContext>();
        var userInDb = await context.Users.FirstAsync(u => u.UserName == UserConstants.DefaultUserDb.Username);

        // Act
        var apiResponse = await client.GetAsync($"/api/users/{userInDb.Id}");

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await ResponseHelper.GetApiResultAsync<UserResponseModel>(apiResponse);
        CheckResponse.Succeeded(response);
        response.Result.Should().NotBeNull();
        response.Result.Id.Should().Be(userInDb.Id);
        response.Result.UserName.Should().Be(userInDb.UserName);
        response.Result.Email.Should().Be(userInDb.Email);
    }

    [Fact]
    public async Task GetById_User_Should_Return_NotFound_When_Not_Exists()
    {
        // Arrange
        var client = await _factory.CreateDefaultClientAsync();

        // Act
        var apiResponse = await client.GetAsync($"/api/users/{Guid.NewGuid()}");

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var response = await ResponseHelper.GetApiResultAsync<string>(apiResponse);
        CheckResponse.Failure(response);
    }

    [Fact]
    public async Task CreateInternalUser_Should_Create_Manager_When_Called_By_Admin()
    {
        // Arrange
        var client = await _factory.CreateDefaultClientAsync();
        var model = new CreateInternalUserModel
        {
            UserName = "admin_mgr",
            FullName = "Admin Created Manager",
            Email = "admin_mgr@parking.com",
            Password = "Password.1!",
            PhoneNumber = "0987654321",
            Role = "Manager"
        };

        // Act
        var apiResponse = await client.PostAsync("/api/users/internal", new JsonContent(model));

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await ResponseHelper.GetApiResultAsync<CreateUserResponseModel>(apiResponse);
        CheckResponse.Succeeded(response);
        response.Result.Id.Should().NotBeEmpty();

        var context = _factory.GetRequiredService<DatabaseContext>();
        var userInDb = await context.Users.FirstOrDefaultAsync(u => u.Id == response.Result.Id.ToString());
        userInDb.Should().NotBeNull();
        userInDb.Role.Should().Be("Manager");
        userInDb.IsActive.Should().BeFalse();
        userInDb.EmailConfirmed.Should().BeFalse();
    }

    [Fact]
    public async Task CreateInternalUser_Should_Create_Staff_When_Called_By_Admin()
    {
        // Arrange
        var client = await _factory.CreateDefaultClientAsync();
        var model = new CreateInternalUserModel
        {
            UserName = "admin_stf",
            FullName = "Admin Created Staff",
            Email = "admin_stf@parking.com",
            Password = "Password.1!",
            PhoneNumber = "0987654321",
            Role = "Staff"
        };

        // Act
        var apiResponse = await client.PostAsync("/api/users/internal", new JsonContent(model));

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await ResponseHelper.GetApiResultAsync<CreateUserResponseModel>(apiResponse);
        CheckResponse.Succeeded(response);

        var context = _factory.GetRequiredService<DatabaseContext>();
        var userInDb = await context.Users.FirstOrDefaultAsync(u => u.Id == response.Result.Id.ToString());
        userInDb.Should().NotBeNull();
        userInDb.Role.Should().Be("Staff");
        userInDb.IsActive.Should().BeFalse();
        userInDb.EmailConfirmed.Should().BeFalse();
    }

    [Fact]
    public async Task CreateInternalUser_Should_Return_Forbidden_When_Called_By_Non_Admin()
    {
        // Arrange
        var client = await _factory.CreateClientWithRoleAsync("User");
        var model = new CreateInternalUserModel
        {
            UserName = "forbidden_user",
            Email = "forbidden@parking.com",
            Password = "Password.1!",
            PhoneNumber = "0987654321",
            Role = "Staff"
        };

        // Act
        var apiResponse = await client.PostAsync("/api/users/internal", new JsonContent(model));

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateInternalUser_Should_Return_Unauthorized_When_Called_Without_Token()
    {
        // Arrange
        var client = _factory.CreateUnauthorizedClient();
        var model = new CreateInternalUserModel
        {
            UserName = "no_auth_user",
            Email = "noauth@parking.com",
            Password = "Password.1!",
            PhoneNumber = "0987654321",
            Role = "Staff"
        };

        // Act
        var apiResponse = await client.PostAsync("/api/users/internal", new JsonContent(model));

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateInternalUser_Should_Return_BadRequest_When_Role_Is_Invalid()
    {
        // Arrange
        var client = await _factory.CreateDefaultClientAsync();
        var model = new CreateInternalUserModel
        {
            UserName = "bad_role_user",
            Email = "badrole@parking.com",
            Password = "Password.1!",
            PhoneNumber = "0987654321",
            Role = "Customer"
        };

        // Act
        var apiResponse = await client.PostAsync("/api/users/internal", new JsonContent(model));

        // Assert
        apiResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateStatus_Should_Deactivate_And_Activate_User_When_Called_By_Admin()
    {
        // Arrange
        var client = await _factory.CreateDefaultClientAsync();
        var context = _factory.GetRequiredService<DatabaseContext>();

        // Create a staff user to deactivate
        var staffModel = new CreateInternalUserModel
        {
            UserName = "status_test_user",
            FullName = "Status Test User",
            Email = "statustest@parking.com",
            Password = "Password.1!",
            PhoneNumber = "0987654321",
            Role = "Staff"
        };
        var createResponse = await client.PostAsync("/api/users/internal", new JsonContent(staffModel));
        var createResult = await ResponseHelper.GetApiResultAsync<CreateUserResponseModel>(createResponse);
        var targetUserId = createResult.Result.Id.ToString();

        // Activate the created internal user
        var activateAccountResponse = await client.PostAsync("/api/users/activate", new JsonContent(new ActivateAccountModel
        {
            UserId = targetUserId,
            Token = createResult.Result.ActivationToken,
            NewPassword = "Password.1!",
            ConfirmPassword = "Password.1!"
        }));
        activateAccountResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act 1: Deactivate
        var deactivateResponse = await client.PatchAsync($"/api/users/{targetUserId}/status", new JsonContent(new UpdateUserStatusModel { IsActive = false }));
        deactivateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var userAfterDeactivate = await context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == targetUserId);
        userAfterDeactivate.IsActive.Should().BeFalse();

        // Verify deactivated user cannot log in
        var loginResponse = await client.PostAsync("/api/users/authenticate", new JsonContent(new LoginUserModel
        {
            Username = "status_test_user",
            Password = "Password.1!"
        }));
        loginResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Act 2: Re-activate
        var activateResponse = await client.PatchAsync($"/api/users/{targetUserId}/status", new JsonContent(new UpdateUserStatusModel { IsActive = true }));
        activateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var userAfterActivate = await context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == targetUserId);
        userAfterActivate.IsActive.Should().BeTrue();

        // Verify reactivated user can now log in
        var loginResponse2 = await client.PostAsync("/api/users/authenticate", new JsonContent(new LoginUserModel
        {
            Username = "status_test_user",
            Password = "Password.1!"
        }));
        loginResponse2.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateStatus_Should_Return_BadRequest_When_Admin_Deactivates_Self()
    {
        // Arrange
        var client = await _factory.CreateDefaultClientAsync();
        var context = _factory.GetRequiredService<DatabaseContext>();
        var currentAdmin = await context.Users.FirstAsync(u => u.UserName == UserConstants.DefaultUserDb.Username);

        // Act
        var response = await client.PatchAsync($"/api/users/{currentAdmin.Id}/status", new JsonContent(new UpdateUserStatusModel { IsActive = false }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var result = await ResponseHelper.GetApiResultAsync<string>(response);
        result.Errors.Should().Contain(e => e.Contains("cannot deactivate their own account"));
    }

    [Fact]
    public async Task GetAll_Users_Should_Return_Forbidden_When_Called_By_Non_Admin()
    {
        // Arrange
        var client = await _factory.CreateClientWithRoleAsync("User");

        // Act
        var response = await client.GetAsync("/api/users");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
