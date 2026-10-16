using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using PMS.Application.Exceptions;
using PMS.Application.Models.Admin;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Services;

public class AdminAccountServiceTests : BaseServiceTestConfiguration
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly PmsDbContext _pmsContext;
    private readonly UserService _sut;

    public AdminAccountServiceTests()
    {
        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        _userManager = Substitute.For<UserManager<ApplicationUser>>(userStore, null, null, null, null, null, null, null, null);

        var options = new DbContextOptionsBuilder<PmsDbContext>()
            .UseInMemoryDatabase(databaseName: $"AdminAccountTests_{Guid.NewGuid()}")
            .Options;
        _pmsContext = new PmsDbContext(options);

        _sut = new UserService(Mapper, _userManager, null, Configuration, _pmsContext);
    }

    [Fact]
    public async Task CreateManagerAsync_Should_Create_Manager_And_Generate_QL_Code()
    {
        var userId = Guid.NewGuid().ToString();
        _userManager.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(args =>
            {
                var user = args.Arg<ApplicationUser>();
                user.Id = userId;
                return IdentityResult.Success;
            });

        var model = new CreateEmployeeAccountModel
        {
            UserName = "manager01",
            FullName = "Tran Manager",
            Email = "manager01@test.com",
            Password = "Password123!",
            PhoneNumber = "0901234567"
        };

        var result = await _sut.CreateManagerAsync(model);

        result.Id.Should().Be(Guid.Parse(userId));
        result.EmployeeCode.Should().NotBeNullOrEmpty();
        result.EmployeeCode.Length.Should().Be(8);
        Regex.IsMatch(result.EmployeeCode, @"^QL\d{6}$").Should().BeTrue();

        var employee = await _pmsContext.Employees.FirstOrDefaultAsync(e => e.UserId == userId);
        employee.Should().NotBeNull();
        employee.EmployeeCode.Should().Be(result.EmployeeCode);
        employee.Position.Should().Be("Manager");
        employee.Status.Should().Be("Active");
        employee.FullName.Should().Be("Tran Manager");

        await _userManager.Received(1).CreateAsync(
            Arg.Is<ApplicationUser>(u => u.Role == "Manager" && u.IsActive && u.EmailConfirmed),
            model.Password);
    }

    [Fact]
    public async Task CreateStaffAsync_Should_Create_Staff_And_Generate_NV_Code()
    {
        var userId = Guid.NewGuid().ToString();
        _userManager.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(args =>
            {
                var user = args.Arg<ApplicationUser>();
                user.Id = userId;
                return IdentityResult.Success;
            });

        var model = new CreateEmployeeAccountModel
        {
            UserName = "staff01",
            FullName = "Le Staff",
            Email = "staff01@test.com",
            Password = "Password123!",
            PhoneNumber = "0909876543"
        };

        var result = await _sut.CreateStaffAsync(model);

        result.Id.Should().Be(Guid.Parse(userId));
        result.EmployeeCode.Should().NotBeNullOrEmpty();
        result.EmployeeCode.Length.Should().Be(8);
        Regex.IsMatch(result.EmployeeCode, @"^NV\d{6}$").Should().BeTrue();

        var employee = await _pmsContext.Employees.FirstOrDefaultAsync(e => e.UserId == userId);
        employee.Should().NotBeNull();
        employee.EmployeeCode.Should().Be(result.EmployeeCode);
        employee.Position.Should().Be("Staff");
        employee.Status.Should().Be("Active");
        employee.FullName.Should().Be("Le Staff");

        await _userManager.Received(1).CreateAsync(
            Arg.Is<ApplicationUser>(u => u.Role == "Staff" && u.IsActive && u.EmailConfirmed),
            model.Password);
    }

    [Fact]
    public async Task UpdateUserStatusAsync_Should_Synchronize_Employee_Status_When_Deactivating()
    {
        var adminId = Guid.NewGuid().ToString();
        var staffId = Guid.NewGuid().ToString();

        var staffUser = new ApplicationUser
        {
            Id = staffId,
            UserName = "staff02",
            Role = "Staff",
            IsActive = true
        };

        _userManager.FindByIdAsync(staffId).Returns(Task.FromResult(staffUser));
        _userManager.UpdateAsync(staffUser).Returns(Task.FromResult(IdentityResult.Success));

        var employee = new Employee
        {
            UserId = staffId,
            EmployeeCode = "NV123456",
            FullName = "Staff Member",
            Position = "Staff",
            Status = "Active",
            CreatedAt = DateTime.UtcNow
        };
        _pmsContext.Employees.Add(employee);
        await _pmsContext.SaveChangesAsync();

        var result = await _sut.UpdateUserStatusAsync(adminId, staffId, false);

        result.Should().BeTrue();
        staffUser.IsActive.Should().BeFalse();

        var updatedEmployee = await _pmsContext.Employees.FirstOrDefaultAsync(e => e.UserId == staffId);
        updatedEmployee.Status.Should().Be("Inactive");
        updatedEmployee.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateUserStatusAsync_Admin_Self_Deactivation_Should_Throw_BadRequest()
    {
        var adminId = Guid.NewGuid().ToString();

        var act = async () => await _sut.UpdateUserStatusAsync(adminId, adminId, false);

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("Administrators cannot deactivate their own account.");
    }

    [Fact]
    public async Task UpdateUserStatusAsync_Deactivating_Another_Admin_Should_Throw_BadRequest()
    {
        var admin1Id = Guid.NewGuid().ToString();
        var admin2Id = Guid.NewGuid().ToString();

        var admin2User = new ApplicationUser
        {
            Id = admin2Id,
            UserName = "anotheradmin",
            Role = "Admin",
            IsActive = true
        };

        _userManager.FindByIdAsync(admin2Id).Returns(Task.FromResult(admin2User));

        var act = async () => await _sut.UpdateUserStatusAsync(admin1Id, admin2Id, false);

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("Cannot deactivate an Administrator account.");
    }

    [Fact]
    public async Task CreateInternalUser_CompensatingRollback_WhenDbFails_Should_DeleteIdentityUser()
    {
        var userId = Guid.NewGuid().ToString();
        var user = new ApplicationUser
        {
            Id = userId,
            UserName = "fail_user",
            Role = "Staff",
            IsActive = false,
            EmailConfirmed = false
        };

        _userManager.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(args =>
            {
                var u = args.Arg<ApplicationUser>();
                u.Id = userId;
                return IdentityResult.Success;
            });

        _userManager.DeleteAsync(Arg.Any<ApplicationUser>())
            .Returns(IdentityResult.Success);

        // Dispose context to simulate database connection/save failure
        _pmsContext.Dispose();

        var model = new CreateEmployeeAccountModel
        {
            UserName = "fail_user",
            FullName = "Fail User",
            Email = "fail@test.com",
            Password = "Password123!",
            PhoneNumber = "0901234567"
        };

        var act = async () => await _sut.CreateStaffAsync(model);

        await act.Should().ThrowAsync<Exception>();
        await _userManager.Received(1).DeleteAsync(Arg.Is<ApplicationUser>(u => u.Id == userId));
    }
}
