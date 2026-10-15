using FluentValidation.TestHelper;
using Microsoft.AspNetCore.Identity;
using PMS.Application.Models.User;
using PMS.Application.Models.Validators.User;
using PMS.DataAccess.Identity;
using NSubstitute;
using Xunit;

namespace PMS.Application.UnitTests.Validators;

public class CreateInternalUserModelValidatorTests
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly CreateInternalUserModelValidator _sut;

    public CreateInternalUserModelValidatorTests()
    {
        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        _userManager = Substitute.For<UserManager<ApplicationUser>>(userStore, null, null, null, null, null, null, null, null);
        _sut = new CreateInternalUserModelValidator(_userManager);
    }

    [Theory]
    [InlineData("Manager")]
    [InlineData("Staff")]
    [InlineData("manager")]
    [InlineData("staff")]
    public void Validator_Should_Pass_For_Allowed_Roles(string role)
    {
        // Arrange
        var model = new CreateInternalUserModel
        {
            UserName = "validInternalUser",
            FullName = "Internal User",
            Email = "internal@example.com",
            Password = "Password.1!",
            PhoneNumber = "0987654321",
            Role = role
        };
        _userManager.FindByNameAsync(model.UserName).Returns((ApplicationUser)null);
        _userManager.FindByEmailAsync(model.Email).Returns((ApplicationUser)null);

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldNotHaveValidationErrorFor(u => u.Role);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    [InlineData("User")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Validator_Should_Fail_For_Disallowed_Roles(string role)
    {
        // Arrange
        var model = new CreateInternalUserModel { Role = role };

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(u => u.Role);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    [InlineData("ab")]
    public void Validator_Should_Fail_For_Invalid_UserName(string username)
    {
        // Arrange
        var model = new CreateInternalUserModel { UserName = username };

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(u => u.UserName);
    }

    [Theory]
    [InlineData("bademail")]
    [InlineData("test@")]
    [InlineData("")]
    public void Validator_Should_Fail_For_Invalid_Email(string email)
    {
        // Arrange
        var model = new CreateInternalUserModel { Email = email };

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(u => u.Email);
    }

    [Theory]
    [InlineData("123456789")]
    [InlineData("invalidphone")]
    [InlineData("")]
    public void Validator_Should_Fail_For_Invalid_PhoneNumber(string phone)
    {
        // Arrange
        var model = new CreateInternalUserModel { PhoneNumber = phone };

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(u => u.PhoneNumber);
    }

    [Fact]
    public void Validator_Should_Fail_When_Username_Already_Exists()
    {
        // Arrange
        var model = new CreateInternalUserModel { UserName = "existingUser" };
        _userManager.FindByNameAsync(model.UserName).Returns(new ApplicationUser());

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(u => u.UserName)
            .WithErrorMessage("Username is not available");
    }

    [Fact]
    public void Validator_Should_Fail_When_Email_Already_Exists()
    {
        // Arrange
        var model = new CreateInternalUserModel { Email = "existing@example.com" };
        _userManager.FindByEmailAsync(model.Email).Returns(new ApplicationUser());

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(u => u.Email)
            .WithErrorMessage("Email address is already in use");
    }
}
