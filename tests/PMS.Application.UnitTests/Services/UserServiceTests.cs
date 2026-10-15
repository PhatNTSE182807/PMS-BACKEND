using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using MockQueryable;
using PMS.Application.Exceptions;
using PMS.Application.Models.User;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Identity;
using NSubstitute;
using Xunit;

namespace PMS.Application.UnitTests.Services;

public class UserServiceTests : BaseServiceTestConfiguration
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserService _sut;
    private readonly UserManager<ApplicationUser> _userManager;

    public UserServiceTests()
    {
        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        _userManager =
            Substitute.For<UserManager<ApplicationUser>>(userStore, null, null, null, null, null, null, null, null);
        var contextAccessor = Substitute.For<IHttpContextAccessor>();
        var userPrincipalFactory = Substitute.For<IUserClaimsPrincipalFactory<ApplicationUser>>();
        _signInManager = Substitute.For<SignInManager<ApplicationUser>>(_userManager, contextAccessor,
            userPrincipalFactory, null, null, null, null);

        _sut = new UserService(Mapper, _userManager, _signInManager, Configuration);
    }

    [Fact]
    public async Task CreateAsync_Should_Add_User_To_Database()
    {
        // Arrange
        var userId = Guid.NewGuid().ToString();
        var createUserModel = Builder<CreateUserModel>.CreateNew().Build();
        _userManager.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(args =>
            {
                var user = args.Arg<ApplicationUser>();
                user.Id = userId;
                return IdentityResult.Success;
            });

        // Act
        var result = await _sut.CreateAsync(createUserModel);

        // Assert
        result.Id.Should().Be(Guid.Parse(userId));
        await _userManager.Received(1).CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>());
    }

    [Fact]
    public async Task CreateAsync_Should_Throw_Exception_If_CreateAsync_Return_Failure()
    {
        // Arrange
        var createUserModel = Builder<CreateUserModel>.CreateNew().Build();
        var identityErrors = Builder<IdentityError>.CreateListOfSize(5).Build().ToArray();
        _userManager.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(IdentityResult.Failed(identityErrors));

        // Act
        Func<Task> callCreateAsync = async () => await _sut.CreateAsync(createUserModel);

        // Assert
        await callCreateAsync.Should().ThrowAsync<BadRequestException>()
            .WithMessage(identityErrors.FirstOrDefault()?.Description);
        await _userManager.Received(1).CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>());
    }

    [Fact]
    public async Task LoginAsync_Should_Throw_Exception_If_User_Does_Not_Exist()
    {
        // Arrange
        var loginUserModel = Builder<LoginUserModel>.CreateNew().Build();
        var emptyUserListQueryable = new List<ApplicationUser>().AsQueryable().BuildMock();
        _userManager.Users.Returns(emptyUserListQueryable);

        // Act
        Func<Task> callCreateAsync = async () => await _sut.LoginAsync(loginUserModel);

        // Assert
        await callCreateAsync.Should().ThrowAsync<NotFoundException>().WithMessage("Username or password is incorrect");
    }

    [Fact]
    public async Task LoginAsync_Should_Throw_Exception_If_User_Does_Not_Provide_Good_Credentials()
    {
        // Arrange
        var loginUserModel = Builder<LoginUserModel>.CreateNew().Build();
        var usersListQueryable = Builder<ApplicationUser>.CreateListOfSize(1)
            .All().With(u => u.UserName = loginUserModel.Username)
            .With(u => u.IsActive = true)
            .Build().AsQueryable().BuildMock();
        _userManager.Users.Returns(usersListQueryable);
        _signInManager
            .PasswordSignInAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(SignInResult.Failed);

        // Act
        Func<Task> callCreateAsync = async () => await _sut.LoginAsync(loginUserModel);

        // Assert
        await callCreateAsync.Should().ThrowAsync<BadRequestException>()
            .WithMessage("Username or password is incorrect");
        await _signInManager.Received(1).PasswordSignInAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task LoginAsync_Should_Throw_Exception_If_User_Is_Deactivated()
    {
        // Arrange
        var loginUserModel = Builder<LoginUserModel>.CreateNew().Build();
        var usersListQueryable = Builder<ApplicationUser>.CreateListOfSize(1)
            .All().With(u => u.UserName = loginUserModel.Username)
            .With(u => u.IsActive = false)
            .Build().AsQueryable().BuildMock();
        _userManager.Users.Returns(usersListQueryable);

        // Act
        Func<Task> act = async () => await _sut.LoginAsync(loginUserModel);

        // Assert
        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("Account is deactivated. Please contact an administrator.");
    }

    [Fact]
    public async Task LoginAsync_Should_Return_Login_Response_If_Credentials_Are_Good()
    {
        // Arrange
        var loginUserModel = Builder<LoginUserModel>.CreateNew().Build();
        var usersList = Builder<ApplicationUser>.CreateListOfSize(1).All()
            .With(u => u.UserName = loginUserModel.Username)
            .With(u => u.IsActive = true).Build();
        var usersListQueryable = usersList.AsQueryable().BuildMock();
        _userManager.Users.Returns(usersListQueryable);
        _signInManager
            .PasswordSignInAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(SignInResult.Success);

        // Act
        var result = await _sut.LoginAsync(loginUserModel);

        // Assert
        result.Username.Should().Be(usersList[0].UserName);
        result.Email.Should().Be(usersList[0].Email);
        result.Token.Should().NotBeNullOrEmpty();
        await _signInManager.Received(1).PasswordSignInAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task CreateInternalUserAsync_Should_Create_Manager_Account()
    {
        // Arrange
        var userId = Guid.NewGuid().ToString();
        var model = new CreateInternalUserModel
        {
            UserName = "manager_user",
            FullName = "Manager Test",
            Email = "manager@parking.com",
            Password = "Password.1!",
            PhoneNumber = "0987654321",
            Role = "Manager"
        };

        _userManager.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(args =>
            {
                var user = args.Arg<ApplicationUser>();
                user.Id = userId;
                user.Role.Should().Be("Manager");
                user.IsActive.Should().BeFalse();
                user.EmailConfirmed.Should().BeFalse();
                return IdentityResult.Success;
            });

        // Act
        var result = await _sut.CreateInternalUserAsync(model);

        // Assert
        result.Id.Should().Be(Guid.Parse(userId));
        await _userManager.Received(1).CreateAsync(Arg.Any<ApplicationUser>(), "Password.1!");
    }

    [Fact]
    public async Task CreateInternalUserAsync_Should_Create_Staff_Account()
    {
        // Arrange
        var userId = Guid.NewGuid().ToString();
        var model = new CreateInternalUserModel
        {
            UserName = "staff_user",
            FullName = "Staff Test",
            Email = "staff@parking.com",
            Password = "Password.1!",
            PhoneNumber = "0987654321",
            Role = "Staff"
        };

        _userManager.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(args =>
            {
                var user = args.Arg<ApplicationUser>();
                user.Id = userId;
                user.Role.Should().Be("Staff");
                user.IsActive.Should().BeFalse();
                user.EmailConfirmed.Should().BeFalse();
                return IdentityResult.Success;
            });

        // Act
        var result = await _sut.CreateInternalUserAsync(model);

        // Assert
        result.Id.Should().Be(Guid.Parse(userId));
        await _userManager.Received(1).CreateAsync(Arg.Any<ApplicationUser>(), "Password.1!");
    }

    [Fact]
    public async Task CreateInternalUserAsync_Should_Throw_When_Role_Is_Invalid()
    {
        // Arrange
        var model = new CreateInternalUserModel
        {
            UserName = "invalid_user",
            Email = "invalid@parking.com",
            Password = "Password.1!",
            PhoneNumber = "0987654321",
            Role = "Customer"
        };

        // Act
        Func<Task> act = async () => await _sut.CreateInternalUserAsync(model);

        // Assert
        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("Role must be either 'Manager' or 'Staff'");
    }

    [Fact]
    public async Task UpdateUserStatusAsync_Should_Deactivate_User_Successfully()
    {
        // Arrange
        var adminId = Guid.NewGuid().ToString();
        var targetId = Guid.NewGuid().ToString();
        var targetUser = new ApplicationUser
        {
            Id = targetId,
            UserName = "staff1",
            Role = "Staff",
            IsActive = true
        };

        _userManager.FindByIdAsync(targetId).Returns(targetUser);
        _userManager.UpdateAsync(targetUser).Returns(IdentityResult.Success);

        // Act
        var result = await _sut.UpdateUserStatusAsync(adminId, targetId, false);

        // Assert
        result.Should().BeTrue();
        targetUser.IsActive.Should().BeFalse();
        await _userManager.Received(1).UpdateAsync(targetUser);
    }

    [Fact]
    public async Task UpdateUserStatusAsync_Should_Activate_User_Successfully()
    {
        // Arrange
        var adminId = Guid.NewGuid().ToString();
        var targetId = Guid.NewGuid().ToString();
        var targetUser = new ApplicationUser
        {
            Id = targetId,
            UserName = "staff1",
            Role = "Staff",
            IsActive = false
        };

        _userManager.FindByIdAsync(targetId).Returns(targetUser);
        _userManager.UpdateAsync(targetUser).Returns(IdentityResult.Success);

        // Act
        var result = await _sut.UpdateUserStatusAsync(adminId, targetId, true);

        // Assert
        result.Should().BeTrue();
        targetUser.IsActive.Should().BeTrue();
        await _userManager.Received(1).UpdateAsync(targetUser);
    }

    [Fact]
    public async Task UpdateUserStatusAsync_Should_Throw_When_Admin_Deactivates_Self()
    {
        // Arrange
        var adminId = Guid.NewGuid().ToString();

        // Act
        Func<Task> act = async () => await _sut.UpdateUserStatusAsync(adminId, adminId, false);

        // Assert
        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("Administrators cannot deactivate their own account.");
    }

    [Fact]
    public async Task UpdateUserStatusAsync_Should_Throw_When_Deactivating_Admin_Account()
    {
        // Arrange
        var currentAdminId = Guid.NewGuid().ToString();
        var targetAdminId = Guid.NewGuid().ToString();
        var targetAdmin = new ApplicationUser
        {
            Id = targetAdminId,
            UserName = "other_admin",
            Role = "Admin",
            IsActive = true
        };

        _userManager.FindByIdAsync(targetAdminId).Returns(targetAdmin);

        // Act
        Func<Task> act = async () => await _sut.UpdateUserStatusAsync(currentAdminId, targetAdminId, false);

        // Assert
        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("Cannot deactivate an Administrator account.");
    }

    [Fact]
    public async Task UpdateUserStatusAsync_Should_Throw_When_User_Not_Found()
    {
        // Arrange
        var adminId = Guid.NewGuid().ToString();
        _userManager.FindByIdAsync("unknown").Returns((ApplicationUser)null);

        // Act
        Func<Task> act = async () => await _sut.UpdateUserStatusAsync(adminId, "unknown", false);

        // Assert
        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("User not found.");
    }

    [Fact]
    public async Task GetAllAsync_Should_Return_All_Users()
    {
        // Arrange
        var usersList = Builder<ApplicationUser>.CreateListOfSize(3).All()
            .With(u => u.IsActive = true).Build();
        _userManager.Users.Returns(usersList.AsQueryable().BuildMock());

        // Act
        var result = (await _sut.GetAllAsync()).ToList();

        // Assert
        result.Should().HaveCount(3);
        result[0].UserName.Should().Be(usersList[0].UserName);
        result[1].Email.Should().Be(usersList[1].Email);
        result[2].PhoneNumber.Should().Be(usersList[2].PhoneNumber);
        result[0].IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task GetByIdAsync_Should_Return_User_When_Exists()
    {
        // Arrange
        var user = Builder<ApplicationUser>.CreateNew()
            .With(u => u.IsActive = true).Build();
        _userManager.FindByIdAsync(user.Id).Returns(user);

        // Act
        var result = await _sut.GetByIdAsync(user.Id);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(user.Id);
        result.UserName.Should().Be(user.UserName);
        result.Email.Should().Be(user.Email);
        result.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task GetByIdAsync_Should_Throw_NotFoundException_When_User_Not_Found()
    {
        // Arrange
        _userManager.FindByIdAsync("nonexistent").Returns((ApplicationUser)null);

        // Act
        Func<Task> act = async () => await _sut.GetByIdAsync("nonexistent");

        // Assert
        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("User does not exist");
    }
}
