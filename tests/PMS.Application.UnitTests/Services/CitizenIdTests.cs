using System;
using System.Threading.Tasks;
using FluentAssertions;
using FluentValidation.TestHelper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using PMS.Application.Exceptions;
using PMS.Application.Models.Profile;
using PMS.Application.Models.User;
using PMS.Application.Models.Validators.Profile;
using PMS.Application.Models.Validators.User;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Services;

/// <summary>Citizen ID (CCCD) on registration, the profile and each vehicle.</summary>
public class CitizenIdTests : BaseServiceTestConfiguration
{
    private const string CitizenId = "079123456789";

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly PmsDbContext _pmsContext;
    private readonly UserService _userService;
    private readonly UserProfileService _profileService;

    public CitizenIdTests()
    {
        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        _userManager = Substitute.For<UserManager<ApplicationUser>>(userStore, null, null, null, null, null, null, null, null);
        _userManager.UpdateAsync(Arg.Any<ApplicationUser>()).Returns(IdentityResult.Success);

        var options = new DbContextOptionsBuilder<PmsDbContext>()
            .UseInMemoryDatabase($"CitizenIdTests_{Guid.NewGuid()}")
            .Options;
        _pmsContext = new PmsDbContext(options);
        _pmsContext.VehicleTypes.Add(new VehicleType { Id = 1, Code = "CAR_4", Name = "4-Seat Car", IsActive = true });
        _pmsContext.SaveChanges();

        _userService = new UserService(Mapper, _userManager, null, Configuration, _pmsContext);
        _profileService = new UserProfileService(_userManager, _pmsContext);
    }

    /* Registration */

    [Fact]
    public async Task CreateAsync_Should_Store_The_Citizen_Id_On_The_Customer()
    {
        var userId = Guid.NewGuid().ToString();
        _userManager.CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(args =>
            {
                args.Arg<ApplicationUser>().Id = userId;
                return IdentityResult.Success;
            });

        await _userService.CreateAsync(Registration());

        (await _pmsContext.Customers.SingleAsync(c => c.UserId == userId)).CitizenId.Should().Be(CitizenId);
    }

    [Fact]
    public async Task CreateAsync_Should_Reject_A_Citizen_Id_Already_Registered()
    {
        await AddCustomerAsync(Guid.NewGuid().ToString(), CitizenId);

        var act = () => _userService.CreateAsync(Registration());

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*Citizen ID*");
        await _userManager.DidNotReceive().CreateAsync(Arg.Any<ApplicationUser>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345678901")]
    [InlineData("1234567890123")]
    [InlineData("07912345678a")]
    public void CreateUserModelValidator_Should_Require_12_Digits(string citizenId)
    {
        var validator = new CreateUserModelValidator(_userManager);
        var model = Registration();
        model.CitizenId = citizenId;

        validator.TestValidate(model).ShouldHaveValidationErrorFor(m => m.CitizenId);
    }

    /* Profile */

    [Fact]
    public async Task UpdateProfileAsync_Should_Set_And_Return_The_Citizen_Id()
    {
        var userId = await AddUserAsync(citizenId: null);

        var result = await _profileService.UpdateProfileAsync(userId, new UpdateUserProfileModel { CitizenId = CitizenId });

        result.CitizenId.Should().Be(CitizenId);
    }

    [Fact]
    public async Task UpdateProfileAsync_Should_Reject_A_Citizen_Id_Of_Another_Account()
    {
        await AddCustomerAsync(Guid.NewGuid().ToString(), CitizenId);
        var userId = await AddUserAsync(citizenId: null);

        var act = () => _profileService.UpdateProfileAsync(userId, new UpdateUserProfileModel { CitizenId = CitizenId });

        await act.Should().ThrowAsync<BadRequestException>();
    }

    /* Vehicles */

    [Fact]
    public async Task AddVehicleAsync_Should_Default_To_The_Account_Citizen_Id()
    {
        var userId = await AddUserAsync(CitizenId);

        var result = await _profileService.AddVehicleAsync(userId, Vehicle(citizenId: null));

        result.CitizenId.Should().Be(CitizenId);
    }

    [Fact]
    public async Task AddVehicleAsync_Should_Keep_The_Owner_Citizen_Id_When_Given()
    {
        var userId = await AddUserAsync(CitizenId);

        var result = await _profileService.AddVehicleAsync(userId, Vehicle(citizenId: "001200000001"));

        result.CitizenId.Should().Be("001200000001");
    }

    [Fact]
    public async Task AddVehicleAsync_Should_Require_A_Citizen_Id_When_The_Account_Has_None()
    {
        var userId = await AddUserAsync(citizenId: null);

        var act = () => _profileService.AddVehicleAsync(userId, Vehicle(citizenId: null));

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*Citizen ID*");
    }

    [Fact]
    public void CreateVehicleModelValidator_Should_Reject_A_Malformed_Citizen_Id()
    {
        var result = new CreateVehicleModelValidator().TestValidate(Vehicle(citizenId: "12345"));

        result.ShouldHaveValidationErrorFor(m => m.CitizenId);
    }

    private static CreateUserModel Registration() => new()
    {
        UserName = "citizen01",
        FullName = "Nguyen Van A",
        Email = "citizen01@example.com",
        Password = "Password.1!",
        PhoneNumber = "0987654321",
        CitizenId = CitizenId
    };

    private static CreateVehicleModel Vehicle(string citizenId) => new()
    {
        LicensePlate = "51A-123.45",
        VehicleTypeId = 1,
        CitizenId = citizenId
    };

    private async Task<string> AddUserAsync(string citizenId)
    {
        var userId = Guid.NewGuid().ToString();
        _userManager.FindByIdAsync(userId).Returns(new ApplicationUser { Id = userId, UserName = "owner", IsActive = true });
        await AddCustomerAsync(userId, citizenId);
        return userId;
    }

    private async Task AddCustomerAsync(string userId, string citizenId)
    {
        _pmsContext.Customers.Add(new Customer
        {
            UserId = userId,
            CustomerCode = $"CUS-{Guid.NewGuid():N}"[..12],
            FullName = "Customer",
            CitizenId = citizenId,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await _pmsContext.SaveChangesAsync();
    }
}
