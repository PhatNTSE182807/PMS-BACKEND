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
using PMS.Application.Services;
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
    private readonly IEmailService _emailService;

    public UserServiceTests()
    {
        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        _userManager =
            Substitute.For<UserManager<ApplicationUser>>(userStore, null, null, null, null, null, null, null, null);
        var contextAccessor = Substitute.For<IHttpContextAccessor>();
        var userPrincipalFactory = Substitute.For<IUserClaimsPrincipalFactory<ApplicationUser>>();
        _signInManager = Substitute.For<SignInManager<ApplicationUser>>(_userManager, contextAccessor,
            userPrincipalFactory, null, null, null, null);

        _emailService = Substitute.For<IEmailService>();

        _sut = new UserService(Mapper, _userManager, _signInManager, Configuration, _emailService);
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
        _userManager.FindByEmailAsync(Arg.Any<string>()).Returns((ApplicationUser)null);

        // Act
        Func<Task> callCreateAsync = async () => await _sut.LoginAsync(loginUserModel);

        // Assert
        await callCreateAsync.Should().ThrowAsync<BadRequestException>().WithMessage("Email or password is incorrect");
    }

    [Fact]
    public async Task LoginAsync_Should_Throw_Exception_If_User_Does_Not_Provide_Good_Credentials()
    {
        // Arrange
        var loginUserModel = Builder<LoginUserModel>.CreateNew().Build();
        var user = Builder<ApplicationUser>.CreateNew()
            .With(u => u.Email = loginUserModel.Email)
            .Build();
        _userManager.FindByEmailAsync(loginUserModel.Email).Returns(user);
        _signInManager
            .PasswordSignInAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(SignInResult.Failed);

        // Act
        Func<Task> callCreateAsync = async () => await _sut.LoginAsync(loginUserModel);

        // Assert
        await callCreateAsync.Should().ThrowAsync<BadRequestException>()
            .WithMessage("Email or password is incorrect");
        await _signInManager.Received(1).PasswordSignInAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task LoginAsync_Should_Return_Login_Response_If_Credentials_Are_Good()
    {
        // Arrange
        var loginUserModel = Builder<LoginUserModel>.CreateNew().Build();
        var user = Builder<ApplicationUser>.CreateNew()
            .With(u => u.Email = loginUserModel.Email)
            .Build();
        _userManager.FindByEmailAsync(loginUserModel.Email).Returns(user);
        _signInManager
            .PasswordSignInAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(SignInResult.Success);

        // Act
        var result = await _sut.LoginAsync(loginUserModel);

        // Assert
        result.Username.Should().Be(user.UserName);
        result.Email.Should().Be(user.Email);
        result.Token.Should().NotBeNullOrEmpty();
        await _signInManager.Received(1).PasswordSignInAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task GetAllAsync_Should_Return_All_Users()
    {
        // Arrange
        var usersList = Builder<ApplicationUser>.CreateListOfSize(3).Build();
        _userManager.Users.Returns(usersList.AsQueryable().BuildMock());

        // Act
        var result = (await _sut.GetAllAsync()).ToList();

        // Assert
        result.Should().HaveCount(3);
        result[0].UserName.Should().Be(usersList[0].UserName);
        result[1].Email.Should().Be(usersList[1].Email);
        result[2].PhoneNumber.Should().Be(usersList[2].PhoneNumber);
    }

    [Fact]
    public async Task GetByIdAsync_Should_Return_User_When_Exists()
    {
        // Arrange
        var user = Builder<ApplicationUser>.CreateNew().Build();
        _userManager.FindByIdAsync(user.Id).Returns(user);

        // Act
        var result = await _sut.GetByIdAsync(user.Id);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(user.Id);
        result.UserName.Should().Be(user.UserName);
        result.Email.Should().Be(user.Email);
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
