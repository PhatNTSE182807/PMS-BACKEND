using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using N_Tier.API;
using N_Tier.Api.IntegrationTests.Common;
using N_Tier.Api.IntegrationTests.Common.Constants;
using N_Tier.Api.IntegrationTests.Helpers;
using N_Tier.Application.Models;
using N_Tier.Application.Models.User;
using N_Tier.DataAccess.Persistence;
using Xunit;

namespace N_Tier.Api.IntegrationTests.Tests;

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
}
