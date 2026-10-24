using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using PMS.Application.Exceptions;
using PMS.Application.Models.Profile;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Services;

public class UserProfileServiceTests : IDisposable
{
    private readonly PmsDbContext _pmsContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly UserProfileService _sut;

    public UserProfileServiceTests()
    {
        var options = new DbContextOptionsBuilder<PmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _pmsContext = new PmsDbContext(options);

        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        _userManager = Substitute.For<UserManager<ApplicationUser>>(userStore, null, null, null, null, null, null, null, null);

        _sut = new UserProfileService(_userManager, _pmsContext);
    }

    [Fact]
    public async Task ChangePasswordAsync_Should_Set_A_First_Password_Without_The_Current_One()
    {
        var user = new ApplicationUser { Id = "google-user", Email = "g@example.com", Role = "User" };
        _userManager.FindByIdAsync(user.Id).Returns(user);
        _userManager.HasPasswordAsync(user).Returns(false);
        _userManager.AddPasswordAsync(user, "NewPass@1").Returns(IdentityResult.Success);

        var result = await _sut.ChangePasswordAsync(user.Id, new ChangePasswordModel { CurrentPassword = "", NewPassword = "NewPass@1", ConfirmPassword = "NewPass@1" });

        result.Should().BeTrue();
        await _userManager.Received(1).AddPasswordAsync(user, "NewPass@1");
        await _userManager.DidNotReceiveWithAnyArgs().ChangePasswordAsync(default!, default!, default!);
    }

    [Fact]
    public async Task ChangePasswordAsync_Should_Require_The_Current_Password_When_One_Is_Set()
    {
        var user = new ApplicationUser { Id = "pw-user", Email = "p@example.com", Role = "User", PasswordHash = "hash" };
        _userManager.FindByIdAsync(user.Id).Returns(user);
        _userManager.HasPasswordAsync(user).Returns(true);

        var act = () => _sut.ChangePasswordAsync(user.Id, new ChangePasswordModel { CurrentPassword = " ", NewPassword = "NewPass@1", ConfirmPassword = "NewPass@1" });

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("Current password is required.");
        await _userManager.DidNotReceiveWithAnyArgs().AddPasswordAsync(default!, default!);
    }

    [Fact]
    public async Task ChangePasswordAsync_Should_Change_An_Existing_Password()
    {
        var user = new ApplicationUser { Id = "pw-user", Email = "p@example.com", Role = "User", PasswordHash = "hash" };
        _userManager.FindByIdAsync(user.Id).Returns(user);
        _userManager.HasPasswordAsync(user).Returns(true);
        _userManager.ChangePasswordAsync(user, "OldPass@1", "NewPass@1").Returns(IdentityResult.Success);

        (await _sut.ChangePasswordAsync(user.Id, new ChangePasswordModel { CurrentPassword = "OldPass@1", NewPassword = "NewPass@1", ConfirmPassword = "NewPass@1" }))
            .Should().BeTrue();
    }

    [Fact]
    public async Task GetProfileAsync_Should_Return_UserProfile_When_User_Exists()
    {
        // Arrange
        var userId = Guid.NewGuid().ToString();
        var user = new ApplicationUser
        {
            Id = userId,
            UserName = "john_doe",
            Email = "john@example.com",
            PhoneNumber = "0901234567",
            PasswordHash = "hashed_password",
            Role = "User"
        };

        _userManager.FindByIdAsync(userId).Returns(user);

        var vehicleType = new VehicleType
        {
            Id = 1,
            Code = "CAR_4",
            Name = "4-Seat Car",
            IsActive = true
        };
        _pmsContext.VehicleTypes.Add(vehicleType);

        var customer = new Customer
        {
            UserId = userId,
            CustomerCode = "CUS-001",
            FullName = "John Doe",
            Phone = "0901234567",
            Email = "john@example.com",
            AvatarUrl = "https://example.com/avatar.jpg",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _pmsContext.Customers.Add(customer);
        await _pmsContext.SaveChangesAsync();

        var vehicle = new Vehicle
        {
            CustomerId = customer.Id,
            VehicleTypeId = 1,
            PlateNumber = "51A-123.45",
            PlateNormalized = "51A12345",
            Brand = "Toyota",
            Model = "Camry",
            Color = "White",
            IsDefault = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _pmsContext.Vehicles.Add(vehicle);
        await _pmsContext.SaveChangesAsync();

        // Act
        var result = await _sut.GetProfileAsync(userId);

        // Assert
        result.Should().NotBeNull();
        result.UserId.Should().Be(userId);
        result.FullName.Should().Be("John Doe");
        result.Email.Should().Be("john@example.com");
        result.AvatarUrl.Should().Be("https://example.com/avatar.jpg");
        result.HasPassword.Should().BeTrue();
        result.Status.Should().Be("Active");
        result.Vehicles.Should().HaveCount(1);
        result.Vehicles[0].LicensePlate.Should().Be("51A-123.45");
        result.Vehicles[0].VehicleType.Should().Be("4-Seat Car");
        result.Vehicles[0].IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task GetProfileAsync_Should_Throw_NotFoundException_When_User_Does_Not_Exist()
    {
        // Arrange
        var userId = Guid.NewGuid().ToString();
        _userManager.FindByIdAsync(userId).Returns((ApplicationUser)null);

        // Act
        var act = () => _sut.GetProfileAsync(userId);

        // Assert
        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task UpdateProfileAsync_Should_Update_Profile_Details()
    {
        // Arrange
        var userId = Guid.NewGuid().ToString();
        var user = new ApplicationUser
        {
            Id = userId,
            UserName = "jane_doe",
            Email = "old@example.com",
            PhoneNumber = "0900000000"
        };
        _userManager.FindByIdAsync(userId).Returns(user);

        var customer = new Customer
        {
            UserId = userId,
            CustomerCode = "CUS-002",
            FullName = "Jane Old",
            Email = "old@example.com",
            Phone = "0900000000",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _pmsContext.Customers.Add(customer);
        await _pmsContext.SaveChangesAsync();

        var updateModel = new UpdateUserProfileModel
        {
            FullName = "Jane Updated",
            Email = "new@example.com",
            PhoneNumber = "0911111111",
            AvatarUrl = "https://example.com/new-avatar.png"
        };

        // Act
        var result = await _sut.UpdateProfileAsync(userId, updateModel);

        // Assert
        result.FullName.Should().Be("Jane Updated");
        result.Email.Should().Be("old@example.com");
        result.PhoneNumber.Should().Be("0911111111");
        result.AvatarUrl.Should().Be("https://example.com/new-avatar.png");
    }

    [Fact]
    public async Task AddVehicleAsync_Should_Add_And_Normalize_Vehicle()
    {
        // Arrange
        var userId = Guid.NewGuid().ToString();
        var user = new ApplicationUser { Id = userId, UserName = "car_owner" };
        _userManager.FindByIdAsync(userId).Returns(user);

        var vehicleType = new VehicleType
        {
            Id = 1,
            Code = "CAR_4",
            Name = "4-Seat Car",
            IsActive = true
        };
        _pmsContext.VehicleTypes.Add(vehicleType);

        var customer = new Customer
        {
            UserId = userId,
            CustomerCode = "CUS-CAROWNER",
            FullName = "Car Owner",
            CitizenId = "079123456789",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _pmsContext.Customers.Add(customer);
        await _pmsContext.SaveChangesAsync();

        var createModel = new CreateVehicleModel
        {
            LicensePlate = "51a-999.88",
            VehicleTypeId = 1,
            Brand = "Honda",
            Model = "Civic",
            Color = "Black",
            CitizenId = "079123456789",
            IsDefault = true
        };

        // Act
        var result = await _sut.AddVehicleAsync(userId, createModel);

        // Assert
        result.Should().NotBeNull();
        result.LicensePlate.Should().Be("51A-999.88");
        result.VehicleType.Should().Be("4-Seat Car");
        result.Brand.Should().Be("Honda");
        result.IsDefault.Should().BeTrue();

        var savedVehicle = await _pmsContext.Vehicles.FirstOrDefaultAsync(v => v.Id == result.VehicleId);
        savedVehicle.Should().NotBeNull();
        savedVehicle.PlateNormalized.Should().Be("51A99988");
    }

    [Fact]
    public async Task SetDefaultVehicleAsync_Should_Switch_Default_Vehicle()
    {
        // Arrange
        var userId = Guid.NewGuid().ToString();
        var user = new ApplicationUser { Id = userId, UserName = "owner" };
        _userManager.FindByIdAsync(userId).Returns(user);

        var vehicleType = new VehicleType { Id = 1, Code = "CAR_4", Name = "4-Seat Car", IsActive = true };
        _pmsContext.VehicleTypes.Add(vehicleType);

        var customer = new Customer
        {
            UserId = userId,
            CustomerCode = "CUS-003",
            FullName = "Owner",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _pmsContext.Customers.Add(customer);
        await _pmsContext.SaveChangesAsync();

        var vehicle1 = new Vehicle
        {
            CustomerId = customer.Id,
            VehicleTypeId = 1,
            PlateNumber = "51A-111.11",
            PlateNormalized = "51A11111",
            IsDefault = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var vehicle2 = new Vehicle
        {
            CustomerId = customer.Id,
            VehicleTypeId = 1,
            PlateNumber = "51B-222.22",
            PlateNormalized = "51B22222",
            IsDefault = false,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _pmsContext.Vehicles.AddRange(vehicle1, vehicle2);
        await _pmsContext.SaveChangesAsync();

        // Act
        var result = await _sut.SetDefaultVehicleAsync(userId, vehicle2.Id);

        // Assert
        result.IsDefault.Should().BeTrue();

        var v1InDb = await _pmsContext.Vehicles.FindAsync(vehicle1.Id);
        var v2InDb = await _pmsContext.Vehicles.FindAsync(vehicle2.Id);

        v1InDb.IsDefault.Should().BeFalse();
        v2InDb.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteVehicleAsync_Should_SoftDelete_Vehicle()
    {
        // Arrange
        var userId = Guid.NewGuid().ToString();
        var user = new ApplicationUser { Id = userId, UserName = "owner2" };
        _userManager.FindByIdAsync(userId).Returns(user);

        var vehicleType = new VehicleType { Id = 1, Code = "CAR_4", Name = "4-Seat Car", IsActive = true };
        _pmsContext.VehicleTypes.Add(vehicleType);

        var customer = new Customer
        {
            UserId = userId,
            CustomerCode = "CUS-004",
            FullName = "Owner 2",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _pmsContext.Customers.Add(customer);
        await _pmsContext.SaveChangesAsync();

        var vehicle = new Vehicle
        {
            CustomerId = customer.Id,
            VehicleTypeId = 1,
            PlateNumber = "51C-333.33",
            PlateNormalized = "51C33333",
            IsDefault = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _pmsContext.Vehicles.Add(vehicle);
        await _pmsContext.SaveChangesAsync();

        // Act
        var result = await _sut.DeleteVehicleAsync(userId, vehicle.Id);

        // Assert
        result.Should().BeTrue();

        var vehicleInDb = await _pmsContext.Vehicles.FindAsync(vehicle.Id);
        vehicleInDb.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task UploadAvatarAsync_Should_Throw_When_File_Is_Null()
    {
        var act = () => _sut.UploadAvatarAsync("user-1", null!);
        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("No file was uploaded.");
    }

    [Fact]
    public async Task UploadAvatarAsync_Should_Throw_When_Content_Type_Not_Allowed()
    {
        var file = Substitute.For<Microsoft.AspNetCore.Http.IFormFile>();
        file.Length.Returns(100);
        file.ContentType.Returns("application/pdf");

        var act = () => _sut.UploadAvatarAsync("user-1", file);
        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("Only JPEG, PNG, GIF and WebP images are allowed.");
    }

    [Fact]
    public async Task UploadAvatarAsync_Should_Throw_When_File_Exceeds_Max_Size()
    {
        var file = Substitute.For<Microsoft.AspNetCore.Http.IFormFile>();
        file.Length.Returns(3 * 1024 * 1024); // 3 MB > 2 MB

        var act = () => _sut.UploadAvatarAsync("user-1", file);
        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("Avatar image must be smaller than 2 MB.");
    }

    public void Dispose()
    {
        _pmsContext.Dispose();
    }
}
