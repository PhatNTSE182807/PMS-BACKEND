using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PMS.API;
using PMS.Api.IntegrationTests.Common;
using PMS.Api.IntegrationTests.Helpers;
using PMS.Application.Models;
using PMS.Application.Models.Admin;
using PMS.Application.Models.User;
using PMS.DataAccess.Persistence;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

public class AdminEndpointTests
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    [Fact]
    public async Task CreateManager_WhenAdmin_Should_Return_Success_With_QL_Code()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var model = new CreateEmployeeAccountModel
        {
            UserName = $"mgr_{Guid.NewGuid():N}"[..15],
            FullName = "Admin Created Manager",
            Email = $"mgr_{Guid.NewGuid():N}@test.com",
            Password = "Password.1!",
            PhoneNumber = "0901234567"
        };

        var response = await adminClient.PostAsync("/api/admin/managers", new JsonContent(model));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var apiResult = await ResponseHelper.GetApiResultAsync<CreateUserResponseModel>(response);
        CheckResponse.Succeeded(apiResult);
        apiResult.Result.EmployeeCode.Should().NotBeNullOrEmpty();
        Regex.IsMatch(apiResult.Result.EmployeeCode, @"^QL\d{6}$").Should().BeTrue();

        var pmsContext = _factory.GetRequiredService<PmsDbContext>();
        var employee = await pmsContext.Employees.FirstOrDefaultAsync(e => e.UserId == apiResult.Result.Id.ToString());
        employee.Should().NotBeNull();
        employee.EmployeeCode.Should().Be(apiResult.Result.EmployeeCode);
        employee.Position.Should().Be("Manager");
        employee.Status.Should().Be("PendingActivation");
        apiResult.Result.ActivationToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateStaff_WhenAdmin_Should_Return_Success_With_NV_Code()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var model = new CreateEmployeeAccountModel
        {
            UserName = $"stf_{Guid.NewGuid():N}"[..15],
            FullName = "Admin Created Staff",
            Email = $"stf_{Guid.NewGuid():N}@test.com",
            Password = "Password.1!",
            PhoneNumber = "0909876543"
        };

        var response = await adminClient.PostAsync("/api/admin/staff", new JsonContent(model));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var apiResult = await ResponseHelper.GetApiResultAsync<CreateUserResponseModel>(response);
        CheckResponse.Succeeded(apiResult);
        apiResult.Result.EmployeeCode.Should().NotBeNullOrEmpty();
        Regex.IsMatch(apiResult.Result.EmployeeCode, @"^NV\d{6}$").Should().BeTrue();
        apiResult.Result.ActivationToken.Should().NotBeNullOrEmpty();

        var pmsContext = _factory.GetRequiredService<PmsDbContext>();
        var employee = await pmsContext.Employees.FirstOrDefaultAsync(e => e.UserId == apiResult.Result.Id.ToString());
        employee.Should().NotBeNull();
        employee.EmployeeCode.Should().Be(apiResult.Result.EmployeeCode);
        employee.Position.Should().Be("Staff");
        employee.Status.Should().Be("PendingActivation");
    }

    [Fact]
    public async Task CreateManager_WhenAnonymous_Should_Return_Unauthorized()
    {
        var client = _factory.CreateUnauthorizedClient();
        var model = new CreateEmployeeAccountModel
        {
            UserName = "anonmgr01",
            FullName = "Anon Manager",
            Email = "anonmgr@test.com",
            Password = "Password.1!",
            PhoneNumber = "0901234567"
        };

        var response = await client.PostAsync("/api/admin/managers", new JsonContent(model));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateManager_WhenNonAdmin_Should_Return_Forbidden()
    {
        var userClient = await _factory.CreateClientWithRoleAsync("User");
        var model = new CreateEmployeeAccountModel
        {
            UserName = "usermgr01",
            FullName = "User Role Manager",
            Email = "usermgr@test.com",
            Password = "Password.1!",
            PhoneNumber = "0901234567"
        };

        var response = await userClient.PostAsync("/api/admin/managers", new JsonContent(model));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateStaff_WhenManagerRole_Should_Return_Forbidden()
    {
        var managerClient = await _factory.CreateClientWithRoleAsync("Manager");
        var model = new CreateEmployeeAccountModel
        {
            UserName = "mgrstaff01",
            FullName = "Manager Delegated Staff",
            Email = "mgrstaff@test.com",
            Password = "Password.1!",
            PhoneNumber = "0901234567"
        };

        var response = await managerClient.PostAsync("/api/admin/staff", new JsonContent(model));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetInternalUsers_Should_Return_Only_Manager_And_Staff()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();

        var response = await adminClient.GetAsync("/api/admin/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var apiResult = await ResponseHelper.GetApiResultAsync<IEnumerable<UserResponseModel>>(response);
        CheckResponse.Succeeded(apiResult);

        apiResult.Result.Should().NotContain(u => u.Role == "Admin");
        apiResult.Result.Should().NotContain(u => u.Role == "User");
        apiResult.Result.All(u => u.Role == "Manager" || u.Role == "Staff").Should().BeTrue();
    }

    [Fact]
    public async Task UpdateStatus_Should_Toggle_Activation_And_Block_Deactivated_Login()
    {
        var adminClient = await _factory.CreateDefaultClientAsync();
        var username = $"deact_{Guid.NewGuid():N}"[..15];
        var password = "Password.1!";
        var email = $"{username}@test.com";

        var createResponse = await adminClient.PostAsync("/api/admin/staff", new JsonContent(new CreateEmployeeAccountModel
        {
            UserName = username,
            FullName = "Staff To Deactivate",
            Email = email,
            Password = password,
            PhoneNumber = "0901234567"
        }));
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var createResult = await ResponseHelper.GetApiResultAsync<CreateUserResponseModel>(createResponse);
        var staffId = createResult.Result.Id.ToString();

        var unauthClient = _factory.CreateUnauthorizedClient();

        // 1. Verify unactivated staff CANNOT log in initially
        var initialLoginResponse = await unauthClient.PostAsync("/api/users/authenticate", new JsonContent(new LoginUserModel
        {
            Username = username,
            Password = password
        }));
        initialLoginResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 2. Activate staff via POST /api/users/activate
        var activateAccountResponse = await unauthClient.PostAsync("/api/users/activate", new JsonContent(new ActivateAccountModel
        {
            UserId = staffId,
            Token = createResult.Result.ActivationToken,
            NewPassword = password,
            ConfirmPassword = password
        }));
        activateAccountResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 3. Verify staff can log in after activation
        var loginResponse = await unauthClient.PostAsync("/api/users/authenticate", new JsonContent(new LoginUserModel
        {
            Username = username,
            Password = password
        }));
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 4. Admin deactivates staff via PATCH /api/admin/users/{id}/status
        var patchResponse = await adminClient.PatchAsync($"/api/admin/users/{staffId}/status", new JsonContent(new UpdateUserStatusModel
        {
            IsActive = false
        }));
        patchResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 5. Verify deactivated staff is blocked from logging in
        var deactLoginResponse = await unauthClient.PostAsync("/api/users/authenticate", new JsonContent(new LoginUserModel
        {
            Username = username,
            Password = password
        }));
        deactLoginResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 6. Admin reactivates staff
        var reactivateResponse = await adminClient.PatchAsync($"/api/admin/users/{staffId}/status", new JsonContent(new UpdateUserStatusModel
        {
            IsActive = true
        }));
        reactivateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 7. Verify staff can log in again
        var reactLoginResponse = await unauthClient.PostAsync("/api/users/authenticate", new JsonContent(new LoginUserModel
        {
            Username = username,
            Password = password
        }));
        reactLoginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
