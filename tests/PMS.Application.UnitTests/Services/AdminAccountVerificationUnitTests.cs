using System;
using System.Collections.Generic;
using System.Linq;
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

public class AdminAccountVerificationUnitTests : BaseServiceTestConfiguration
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly PmsDbContext _pmsContext;
    private readonly UserService _sut;

    public AdminAccountVerificationUnitTests()
    {
        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        _userManager = Substitute.For<UserManager<ApplicationUser>>(userStore, null, null, null, null, null, null, null, null);

        var options = new DbContextOptionsBuilder<PmsDbContext>()
            .UseInMemoryDatabase(databaseName: $"AdminAccountVerification_{Guid.NewGuid()}")
            .Options;
        _pmsContext = new PmsDbContext(options);

        _sut = new UserService(Mapper, _userManager, null, Configuration, _pmsContext);
    }

    [Fact]
    public async Task CreateManagerAsync_WhenIdentityCreationFails_Should_Throw_BadRequest_And_Create_No_Employee()
    {
        _userManager.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(IdentityResult.Failed(new IdentityError { Description = "Username 'duplicate_user' is already taken." }));

        var model = new CreateEmployeeAccountModel
        {
            UserName = "duplicate_user",
            FullName = "Dup User",
            Email = "dup@test.com",
            Password = "Password123!",
            PhoneNumber = "0901234567"
        };

        var act = async () => await _sut.CreateManagerAsync(model);

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*already taken*");

        var employeesCount = await _pmsContext.Employees.CountAsync();
        employeesCount.Should().Be(0);
    }

    [Fact]
    public async Task UpdateUserStatusAsync_WhenUserNotFound_Should_Throw_NotFound()
    {
        var adminId = Guid.NewGuid().ToString();
        var nonExistentId = Guid.NewGuid().ToString();

        _userManager.FindByIdAsync(nonExistentId).Returns(Task.FromResult<ApplicationUser>(null));

        var act = async () => await _sut.UpdateUserStatusAsync(adminId, nonExistentId, false);

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*User not found*");

    }

    [Fact]
    public async Task UpdateUserStatusAsync_WhenReactivating_Should_Set_Employee_Status_To_Active()
    {
        var adminId = Guid.NewGuid().ToString();
        var staffId = Guid.NewGuid().ToString();

        var staffUser = new ApplicationUser
        {
            Id = staffId,
            UserName = "staff_inactive",
            Role = "Staff",
            IsActive = false
        };

        _userManager.FindByIdAsync(staffId).Returns(Task.FromResult(staffUser));
        _userManager.UpdateAsync(staffUser).Returns(Task.FromResult(IdentityResult.Success));

        var employee = new Employee
        {
            UserId = staffId,
            EmployeeCode = "NV999999",
            FullName = "Inactive Staff",
            Position = "Staff",
            Status = "Inactive",
            CreatedAt = DateTime.UtcNow
        };
        _pmsContext.Employees.Add(employee);
        await _pmsContext.SaveChangesAsync();

        var result = await _sut.UpdateUserStatusAsync(adminId, staffId, true);

        result.Should().BeTrue();
        staffUser.IsActive.Should().BeTrue();

        var updatedEmployee = await _pmsContext.Employees.FirstOrDefaultAsync(e => e.UserId == staffId);
        updatedEmployee.Status.Should().Be("Active");
    }
}
