using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using PMS.API;
using PMS.Api.IntegrationTests.Common;
using PMS.Api.IntegrationTests.Helpers;
using PMS.Application.Models;
using PMS.Application.Models.Admin;
using PMS.Application.Models.User;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

public class AdminApiConformanceTests
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    [Fact]
    public async Task Admin_Endpoints_Should_Reject_Anonymous_And_NonAdmin_Callers()
    {
        var unauthClient = _factory.CreateUnauthorizedClient();
        var userClient = await _factory.CreateClientWithRoleAsync("User");
        var managerClient = await _factory.CreateClientWithRoleAsync("Manager");

        var dummyModel = new CreateEmployeeAccountModel
        {
            UserName = "test_auth",
            FullName = "Test Auth",
            Email = "auth@test.com",
            Password = "Password.1!",
            PhoneNumber = "0901234567"
        };

        // 1. POST /api/admin/managers
        (await unauthClient.PostAsync("/api/admin/managers", new JsonContent(dummyModel))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await userClient.PostAsync("/api/admin/managers", new JsonContent(dummyModel))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await managerClient.PostAsync("/api/admin/managers", new JsonContent(dummyModel))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 2. POST /api/admin/staff
        (await unauthClient.PostAsync("/api/admin/staff", new JsonContent(dummyModel))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await userClient.PostAsync("/api/admin/staff", new JsonContent(dummyModel))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await managerClient.PostAsync("/api/admin/staff", new JsonContent(dummyModel))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 3. GET /api/admin/users
        (await unauthClient.GetAsync("/api/admin/users")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await userClient.GetAsync("/api/admin/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await managerClient.GetAsync("/api/admin/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 4. PATCH /api/admin/users/{id}/status
        var statusModel = new UpdateUserStatusModel { IsActive = false };
        (await unauthClient.PatchAsync($"/api/admin/users/{Guid.NewGuid()}/status", new JsonContent(statusModel))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await userClient.PatchAsync($"/api/admin/users/{Guid.NewGuid()}/status", new JsonContent(statusModel))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await managerClient.PatchAsync($"/api/admin/users/{Guid.NewGuid()}/status", new JsonContent(statusModel))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateManager_WhenDuplicateUsername_Should_Return_BadRequest()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var username = $"dup_{Guid.NewGuid():N}"[..12];
        var model = new CreateEmployeeAccountModel
        {
            UserName = username,
            FullName = "First Manager",
            Email = $"{username}@test.com",
            Password = "Password.1!",
            PhoneNumber = "0901234567"
        };

        var firstResponse = await adminClient.PostAsync("/api/admin/managers", new JsonContent(model));
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second creation with same username
        var duplicateModel = new CreateEmployeeAccountModel
        {
            UserName = username,
            FullName = "Second Manager",
            Email = $"different_{Guid.NewGuid():N}@test.com",
            Password = "Password.1!",
            PhoneNumber = "0909876543"
        };

        var secondResponse = await adminClient.PostAsync("/api/admin/managers", new JsonContent(duplicateModel));
        secondResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetInternalUsers_Should_Not_Leak_Password_Hashes_Or_Secrets()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();

        var response = await adminClient.GetAsync("/api/admin/users");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var rawJson = (await response.Content.ReadAsStringAsync()).ToLowerInvariant();
        rawJson.Should().NotContain("passwordhash");
        rawJson.Should().NotContain("securitystamp");
        rawJson.Should().NotContain("concurrencystamp");

    }

    [Fact]
    public async Task GetInternalUsers_Filtering_By_Role_And_Status_Should_Work()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();

        // Query by role Manager
        var mgrResponse = await adminClient.GetAsync("/api/admin/users?role=Manager");
        mgrResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var mgrResult = await ResponseHelper.GetApiResultAsync<IEnumerable<UserResponseModel>>(mgrResponse);
        mgrResult.Result.All(u => u.Role == "Manager").Should().BeTrue();

        // Query by role Staff
        var staffResponse = await adminClient.GetAsync("/api/admin/users?role=Staff");
        staffResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var staffResult = await ResponseHelper.GetApiResultAsync<IEnumerable<UserResponseModel>>(staffResponse);
        staffResult.Result.All(u => u.Role == "Staff").Should().BeTrue();
    }

    [Fact]
    public async Task UpdateUserStatus_WhenUserNotFound_Should_Return_NotFound()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var nonExistentId = Guid.NewGuid().ToString();

        var response = await adminClient.PatchAsync($"/api/admin/users/{nonExistentId}/status", new JsonContent(new UpdateUserStatusModel
        {
            IsActive = false
        }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreatedStaff_Should_Be_Active_And_Sign_In_Immediately()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var unauthClient = _factory.CreateUnauthorizedClient();

        var username = $"act_{Guid.NewGuid():N}"[..12];
        var createResponse = await adminClient.PostAsync("/api/admin/staff", new JsonContent(new CreateEmployeeAccountModel
        {
            UserName = username,
            FullName = "Immediate Login Staff",
            Email = $"{username}@test.com",
            Password = "ComplexPassword.1!",
            PhoneNumber = "0901234567"
        }));
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var createResult = await ResponseHelper.GetApiResultAsync<CreateUserResponseModel>(createResponse);
        var userId = createResult.Result.Id.ToString();

        // No activation email: the password the Admin set works straight away
        var loginResponse = await unauthClient.PostAsync("/api/users/authenticate", new JsonContent(new LoginUserModel
        {
            Username = username,
            Password = "ComplexPassword.1!"
        }));
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginResult = await ResponseHelper.GetApiResultAsync<LoginResponseModel>(loginResponse);
        loginResult.Result.Token.Should().NotBeNullOrEmpty();
        loginResult.Result.RequiresTwoFactor.Should().BeFalse();

        var dbContext = _factory.GetRequiredService<PMS.DataAccess.Persistence.DatabaseContext>();
        var userInDb = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            dbContext.Users, u => u.Id == userId);
        userInDb.Should().NotBeNull();
        userInDb.EmailConfirmed.Should().BeTrue();
        userInDb.IsActive.Should().BeTrue();

        var pmsContext = _factory.GetRequiredService<PMS.DataAccess.Persistence.PmsDbContext>();
        var employee = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            pmsContext.Employees, e => e.UserId == userId);
        employee.Should().NotBeNull();
        employee.Status.Should().Be("Active");
    }

    [Fact]
    public async Task CreateStaff_Without_Password_Should_Return_BadRequest()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var username = $"nopwd_{Guid.NewGuid():N}"[..12];

        var createResponse = await adminClient.PostAsync("/api/admin/staff", new JsonContent(new CreateEmployeeAccountModel
        {
            UserName = username,
            FullName = "No Password Staff",
            Email = $"{username}@test.com",
            PhoneNumber = "0901234567"
        }));

        createResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await createResponse.Content.ReadAsStringAsync()).Should().Contain("Password is required");
    }

    [Fact]
    public async Task GetInternalUsers_SearchTerm_Matches_EmployeeFullName_And_EmployeeCode()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var uniqueTag = Guid.NewGuid().ToString("N")[..8];
        var fullName = $"SearchedEmployee_{uniqueTag}";
        var username = $"srch_{uniqueTag}";

        var createResponse = await adminClient.PostAsync("/api/admin/staff", new JsonContent(new CreateEmployeeAccountModel
        {
            UserName = username,
            FullName = fullName,
            Email = $"{username}@test.com",
            Password = "ComplexPassword.1!",
            PhoneNumber = "0901234567"
        }));
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var createResult = await ResponseHelper.GetApiResultAsync<CreateUserResponseModel>(createResponse);
        var employeeCode = createResult.Result.EmployeeCode;

        // Search by FullName
        var searchByNameResponse = await adminClient.GetAsync($"/api/admin/users?searchTerm={fullName}");
        searchByNameResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var searchByNameResult = await ResponseHelper.GetApiResultAsync<IEnumerable<UserResponseModel>>(searchByNameResponse);
        searchByNameResult.Result.Should().Contain(u => u.EmployeeCode == employeeCode);

        // Search by EmployeeCode
        var searchByCodeResponse = await adminClient.GetAsync($"/api/admin/users?searchTerm={employeeCode}");
        searchByCodeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var searchByCodeResult = await ResponseHelper.GetApiResultAsync<IEnumerable<UserResponseModel>>(searchByCodeResponse);
        searchByCodeResult.Result.Should().Contain(u => u.EmployeeCode == employeeCode);
    }
}
