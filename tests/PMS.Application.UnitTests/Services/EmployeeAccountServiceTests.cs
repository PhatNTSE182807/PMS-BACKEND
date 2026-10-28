using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MockQueryable;
using NSubstitute;
using PMS.Application.Exceptions;
using PMS.Application.Models.Account;
using PMS.Application.Models.Admin;
using PMS.Application.Models.User;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Services;

public class EmployeeAccountServiceTests
{
    private const string ManagerId = "manager-1";
    private const string OtherManagerId = "manager-2";
    private const string StaffId = "staff-1";

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IUserService _userService;
    private readonly PmsDbContext _pmsContext;
    private readonly EmployeeAccountService _sut;
    private readonly List<ApplicationUser> _users;

    public EmployeeAccountServiceTests()
    {
        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        _userManager = Substitute.For<UserManager<ApplicationUser>>(userStore, null, null, null, null, null, null, null, null);
        _userService = Substitute.For<IUserService>();

        var options = new DbContextOptionsBuilder<PmsDbContext>()
            .UseInMemoryDatabase($"EmployeeAccountTests_{Guid.NewGuid()}")
            .Options;
        _pmsContext = new PmsDbContext(options);

        _users =
        [
            new ApplicationUser { Id = ManagerId, UserName = "manager01", Email = "m1@test.com", PhoneNumber = "0901000001", Role = "Manager", IsActive = true },
            new ApplicationUser { Id = OtherManagerId, UserName = "manager02", Email = "m2@test.com", PhoneNumber = "0901000002", Role = "Manager", IsActive = true },
            new ApplicationUser { Id = StaffId, UserName = "staff01", Email = "s1@test.com", PhoneNumber = "0901000003", Role = "Staff", IsActive = true }
        ];
        _userManager.Users.Returns(_ => _users.BuildMock());
        _userManager.FindByIdAsync(Arg.Any<string>()).Returns(args => _users.FirstOrDefault(u => u.Id == args.Arg<string>()));
        _userManager.UpdateAsync(Arg.Any<ApplicationUser>()).Returns(IdentityResult.Success);

        _pmsContext.ParkingLots.AddRange(
            Lot(1, "LOT-A", ManagerId),
            Lot(2, "LOT-B", OtherManagerId),
            Lot(3, "LOT-C", null));
        _pmsContext.Employees.AddRange(
            Employee(ManagerId, "QL000001", "Manager One", "Manager", null),
            Employee(OtherManagerId, "QL000002", "Manager Two", "Manager", null),
            Employee(StaffId, "NV000001", "Staff One", "Staff", 1));
        _pmsContext.SaveChanges();

        _sut = new EmployeeAccountService(_userManager, _userService, _pmsContext);
    }

    /* Admin -> Manager */

    [Fact]
    public async Task GetManagersAsync_Should_Return_Managers_With_Their_Lots()
    {
        var result = (await _sut.GetManagersAsync()).ToList();

        result.Should().HaveCount(2);
        result.Single(m => m.Id == ManagerId).ParkingLots.Select(l => l.Id).Should().Equal(1);
        result.Should().OnlyContain(m => m.Role == "Manager");
    }

    [Fact]
    public async Task GetManagersAsync_Should_Filter_By_Search_Term()
    {
        var result = await _sut.GetManagersAsync(searchTerm: "two");

        result.Should().ContainSingle().Which.Id.Should().Be(OtherManagerId);
    }

    [Fact]
    public async Task GetManagerAsync_Should_Throw_NotFound_For_A_Staff_Account()
    {
        var act = () => _sut.GetManagerAsync(StaffId);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task UpdateManagerAsync_Should_Update_Account_And_Employee()
    {
        var model = new UpdateEmployeeAccountModel { FullName = "Manager Renamed", Email = "new@test.com", PhoneNumber = "0909999999" };

        var result = await _sut.UpdateManagerAsync(ManagerId, model);

        result.FullName.Should().Be("Manager Renamed");
        result.Email.Should().Be("new@test.com");
        (await _pmsContext.Employees.SingleAsync(e => e.UserId == ManagerId)).Phone.Should().Be("0909999999");
    }

    [Fact]
    public async Task UpdateManagerAsync_Should_Reject_An_Email_Used_By_Another_Account()
    {
        _userManager.FindByEmailAsync("s1@test.com").Returns(_users.Single(u => u.Id == StaffId));
        var model = new UpdateEmployeeAccountModel { FullName = "Manager One", Email = "s1@test.com", PhoneNumber = "0901000001" };

        var act = () => _sut.UpdateManagerAsync(ManagerId, model);

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*Email*");
    }

    [Fact]
    public async Task UpdateManagerAsync_Should_Reject_A_Phone_Used_By_Another_Account()
    {
        var model = new UpdateEmployeeAccountModel { FullName = "Manager One", Email = "m1@test.com", PhoneNumber = "0901000003" };

        var act = () => _sut.UpdateManagerAsync(ManagerId, model);

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*Phone*");
    }

    [Fact]
    public async Task SetManagerStatusAsync_Should_Deactivate_And_Keep_Lots()
    {
        var result = await _sut.SetManagerStatusAsync(ManagerId, false);

        result.IsActive.Should().BeFalse();
        result.Status.Should().Be("Inactive");
        result.ParkingLots.Should().ContainSingle();
    }

    [Fact]
    public async Task AssignManagerLotsAsync_Should_Assign_Free_Lots_And_Release_The_Others()
    {
        var result = await _sut.AssignManagerLotsAsync(ManagerId, new AssignParkingLotsModel { ParkingLotIds = [3] });

        result.ParkingLots.Select(l => l.Id).Should().BeEquivalentTo([3]);
        (await _pmsContext.ParkingLots.FindAsync(1)).ManagerUserId.Should().BeNull();
        (await _pmsContext.ParkingLots.FindAsync(3)).ManagerUserId.Should().Be(ManagerId);
    }

    [Fact]
    public async Task AssignManagerLotsAsync_Should_Refuse_A_Lot_That_Already_Has_An_Active_Manager()
    {
        var act = () => _sut.AssignManagerLotsAsync(ManagerId, new AssignParkingLotsModel { ParkingLotIds = [2] });

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*only have one Manager*");
        (await _pmsContext.ParkingLots.FindAsync(2)).ManagerUserId.Should().Be(OtherManagerId);
        (await _pmsContext.ParkingLots.FindAsync(1)).ManagerUserId.Should().Be(ManagerId);
    }

    [Fact]
    public async Task AssignManagerLotsAsync_Should_Hand_Over_The_Lot_Of_A_Deactivated_Manager()
    {
        (await _pmsContext.Employees.SingleAsync(e => e.UserId == OtherManagerId)).Status = "Inactive";
        await _pmsContext.SaveChangesAsync();

        var result = await _sut.AssignManagerLotsAsync(ManagerId, new AssignParkingLotsModel { ParkingLotIds = [1, 2] });

        result.ParkingLots.Select(l => l.Id).Should().BeEquivalentTo([1, 2]);
        (await _pmsContext.ParkingLots.FindAsync(2)).ManagerUserId.Should().Be(ManagerId);
    }

    [Fact]
    public async Task AssignManagerLotsAsync_Should_Throw_For_An_Unknown_Lot()
    {
        var act = () => _sut.AssignManagerLotsAsync(ManagerId, new AssignParkingLotsModel { ParkingLotIds = [99] });

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task AssignManagerLotsAsync_Should_Reject_A_Deactivated_Manager()
    {
        _users.Single(u => u.Id == ManagerId).IsActive = false;

        var act = () => _sut.AssignManagerLotsAsync(ManagerId, new AssignParkingLotsModel { ParkingLotIds = [3] });

        await act.Should().ThrowAsync<BadRequestException>();
    }

    /* Manager -> Staff */

    [Fact]
    public async Task GetManagedLotsAsync_Should_Return_Only_The_Managers_Lots()
    {
        var result = await _sut.GetManagedLotsAsync(ManagerId);

        result.Should().ContainSingle().Which.Id.Should().Be(1);
    }

    [Fact]
    public async Task GetStaffLotsAsync_Should_Return_The_Staffs_Own_Lot()
    {
        (await _sut.GetStaffLotsAsync(StaffId)).Should().ContainSingle().Which.Id.Should().Be(1);
        (await _sut.GetStaffLotsAsync(ManagerId)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetStaffAsync_Should_Return_Staff_Of_Managed_Lots_Only()
    {
        (await _sut.GetStaffAsync(ManagerId)).Should().ContainSingle().Which.Id.Should().Be(StaffId);
        (await _sut.GetStaffAsync(OtherManagerId)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetStaffAsync_Should_Forbid_A_Lot_The_Manager_Does_Not_Manage()
    {
        var act = () => _sut.GetStaffAsync(ManagerId, parkingLotId: 2);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task GetStaffByIdAsync_Should_Forbid_Staff_Of_Another_Managers_Lot()
    {
        var act = () => _sut.GetStaffByIdAsync(OtherManagerId, StaffId);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task CreateStaffAsync_Should_Create_Staff_In_The_Managers_Lot()
    {
        var newId = Guid.NewGuid();
        _userService.CreateStaffAsync(Arg.Any<CreateEmployeeAccountModel>()).Returns(_ =>
        {
            _users.Add(new ApplicationUser { Id = newId.ToString(), UserName = "newstaff", Email = "newstaff@test.com", PhoneNumber = "0907777777", Role = "Staff", IsActive = true });
            _pmsContext.Employees.Add(Employee(newId.ToString(), "NV000002", "New Staff", "Staff", null));
            _pmsContext.SaveChanges();
            return new CreateUserResponseModel { Id = newId, EmployeeCode = "NV000002" };
        });
        var model = new CreateStaffAccountModel
        {
            FullName = "New Staff",
            Email = "newstaff@test.com",
            PhoneNumber = "0907777777",
            Password = "Password1",
            ConfirmPassword = "Password1",
            ParkingLotId = 1
        };

        var result = await _sut.CreateStaffAsync(ManagerId, model);

        result.Id.Should().Be(newId.ToString());
        result.ParkingLots.Should().ContainSingle().Which.Id.Should().Be(1);
        (await _pmsContext.Employees.SingleAsync(e => e.UserId == newId.ToString())).ParkingLotId.Should().Be(1);
        await _userService.Received(1).CreateStaffAsync(Arg.Is<CreateEmployeeAccountModel>(m => m.UserName == "newstaff"));
    }

    [Fact]
    public async Task CreateStaffAsync_Should_Forbid_A_Lot_Of_Another_Manager()
    {
        var model = new CreateStaffAccountModel { FullName = "X Y", Email = "x@test.com", PhoneNumber = "0907777777", Password = "Password1", ParkingLotId = 2 };

        var act = () => _sut.CreateStaffAsync(ManagerId, model);

        await act.Should().ThrowAsync<ForbiddenException>();
        await _userService.DidNotReceive().CreateStaffAsync(Arg.Any<CreateEmployeeAccountModel>());
    }

    [Fact]
    public async Task CreateStaffAsync_Should_Forbid_A_Lot_Without_A_Manager()
    {
        var model = new CreateStaffAccountModel { FullName = "X Y", Email = "x@test.com", PhoneNumber = "0907777777", Password = "Password1", ParkingLotId = 3 };

        var act = () => _sut.CreateStaffAsync(ManagerId, model);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task CreateStaffAsync_Should_Reject_A_Phone_Already_In_Use()
    {
        var model = new CreateStaffAccountModel { FullName = "X Y", Email = "x@test.com", PhoneNumber = "0901000003", Password = "Password1", ParkingLotId = 1 };

        var act = () => _sut.CreateStaffAsync(ManagerId, model);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task UpdateStaffAsync_Should_Update_Details()
    {
        var model = new UpdateEmployeeAccountModel { FullName = "Staff Renamed", Email = "s1@test.com", PhoneNumber = "0901000003" };

        var result = await _sut.UpdateStaffAsync(ManagerId, StaffId, model);

        result.FullName.Should().Be("Staff Renamed");
    }

    [Fact]
    public async Task AssignStaffLotAsync_Should_Move_Staff_To_Another_Managed_Lot()
    {
        _pmsContext.ParkingLots.Find(3)!.ManagerUserId = ManagerId;
        await _pmsContext.SaveChangesAsync();

        var result = await _sut.AssignStaffLotAsync(ManagerId, StaffId, new AssignStaffParkingLotModel { ParkingLotId = 3 });

        result.ParkingLots.Should().ContainSingle().Which.Id.Should().Be(3);
    }

    [Fact]
    public async Task AssignStaffLotAsync_Should_Forbid_A_Lot_Of_Another_Manager()
    {
        var act = () => _sut.AssignStaffLotAsync(ManagerId, StaffId, new AssignStaffParkingLotModel { ParkingLotId = 2 });

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task SetStaffStatusAsync_Should_Lock_The_Account()
    {
        var result = await _sut.SetStaffStatusAsync(ManagerId, StaffId, false);

        result.IsActive.Should().BeFalse();
        (await _pmsContext.Employees.SingleAsync(e => e.UserId == StaffId)).Status.Should().Be("Inactive");
    }

    [Fact]
    public async Task DeleteStaffAsync_Should_Soft_Delete_And_Hide_From_The_List()
    {
        var result = await _sut.DeleteStaffAsync(ManagerId, StaffId);

        result.Should().BeTrue();
        var employee = await _pmsContext.Employees.SingleAsync(e => e.UserId == StaffId);
        employee.Status.Should().Be("Terminated");
        employee.TerminationDate.Should().NotBeNull();
        _users.Single(u => u.Id == StaffId).IsActive.Should().BeFalse();
        (await _sut.GetStaffAsync(ManagerId)).Should().BeEmpty();
    }

    private static ParkingLot Lot(int id, string code, string managerUserId) => new()
    {
        Id = id,
        Code = code,
        Name = $"Lot {code}",
        Address = "1 Test Street",
        Status = "Active",
        TimeZoneId = "SE Asia Standard Time",
        ManagerUserId = managerUserId,
        CreatedAt = DateTime.UtcNow
    };

    private static Employee Employee(string userId, string code, string fullName, string position, int? lotId) => new()
    {
        UserId = userId,
        EmployeeCode = code,
        FullName = fullName,
        Position = position,
        Status = "Active",
        ParkingLotId = lotId,
        CreatedAt = DateTime.UtcNow
    };
}
