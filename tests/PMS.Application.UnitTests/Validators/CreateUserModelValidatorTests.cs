using FluentValidation.TestHelper;
using Microsoft.AspNetCore.Identity;
using PMS.Application.Models.User;
using PMS.Application.Models.Validators.User;
using PMS.DataAccess.Identity;
using NSubstitute;
using Xunit;

namespace PMS.Application.UnitTests.Validators;

public class CreateUserModelValidatorTests
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly CreateUserModelValidator _sut;

    public CreateUserModelValidatorTests()
    {
        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        _userManager = Substitute.For<UserManager<ApplicationUser>>(userStore, null, null, null, null, null, null, null, null);
        _sut = new CreateUserModelValidator(_userManager);
    }

    [Fact]
    public void Validator_Should_Not_Have_Error_When_All_Fields_Are_Valid()
    {
        // Arrange
        var model = new CreateUserModel
        {
            UserName = "validUser",
            Email = "validuser@example.com",
            Password = "Password.1!",
            PhoneNumber = "0987654321"
        };
        _userManager.FindByNameAsync(model.UserName).Returns((ApplicationUser)null);
        _userManager.FindByEmailAsync(model.Email).Returns((ApplicationUser)null);

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Validator_Should_Have_Error_When_UserName_Is_Empty(string username)
    {
        // Arrange
        var model = new CreateUserModel { UserName = username };

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(u => u.UserName);
    }

    [Fact]
    public void Validator_Should_Have_Error_When_UserName_Is_Too_Short()
    {
        // Arrange
        var model = new CreateUserModel { UserName = "abcd" };

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(u => u.UserName);
    }

    [Fact]
    public void Validator_Should_Have_Error_When_UserName_Is_Too_Long()
    {
        // Arrange
        var model = new CreateUserModel { UserName = new string('a', 21) };

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(u => u.UserName);
    }

    [Fact]
    public void Validator_Should_Have_Error_When_UserName_Is_Already_Taken()
    {
        // Arrange
        var model = new CreateUserModel { UserName = "existingUser" };
        _userManager.FindByNameAsync("existingUser").Returns(new ApplicationUser { UserName = "existingUser" });

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(u => u.UserName)
            .WithErrorMessage("Username is not available");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Validator_Should_Have_Error_When_Email_Is_Empty(string email)
    {
        // Arrange
        var model = new CreateUserModel { Email = email };

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(u => u.Email);
    }

    [Theory]
    [InlineData("invalidEmail")]
    [InlineData("user@")]
    [InlineData("@domain.com")]
    [InlineData("user@domain")]
    [InlineData("user@domain.")]
    [InlineData("user..name@domain.com")]
    public void Validator_Should_Have_Error_When_Email_Format_Is_Invalid(string email)
    {
        // Arrange
        var model = new CreateUserModel { Email = email };

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(u => u.Email);
    }

    [Theory]
    [InlineData("test@gmail.com")]
    [InlineData("user.name@company.vn")]
    [InlineData("customer123@sub.domain.org")]
    public void Validator_Should_Not_Have_Error_When_Email_Format_Is_Valid(string email)
    {
        // Arrange
        var model = new CreateUserModel { Email = email };
        _userManager.FindByEmailAsync(email).Returns((ApplicationUser)null);

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldNotHaveValidationErrorFor(u => u.Email);
    }

    [Fact]
    public void Validator_Should_Have_Error_When_Email_Is_Already_In_Use()
    {
        // Arrange
        var model = new CreateUserModel { Email = "taken@example.com" };
        _userManager.FindByEmailAsync("taken@example.com").Returns(new ApplicationUser { Email = "taken@example.com" });

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(u => u.Email)
            .WithErrorMessage("Email address is already in use");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Validator_Should_Have_Error_When_Password_Is_Empty(string password)
    {
        // Arrange
        var model = new CreateUserModel { Password = password };

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(u => u.Password);
    }

    [Fact]
    public void Validator_Should_Have_Error_When_Password_Is_Too_Short()
    {
        // Arrange
        var model = new CreateUserModel { Password = "12345" };

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(u => u.Password);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Validator_Should_Have_Error_When_PhoneNumber_Is_Empty(string phoneNumber)
    {
        // Arrange
        var model = new CreateUserModel { PhoneNumber = phoneNumber };

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(u => u.PhoneNumber);
    }

    [Theory]
    [InlineData("0987654321")]    // 10 digits starting with 09
    [InlineData("0901234567")]    // Mobifone 090
    [InlineData("0912345678")]    // Vinaphone 091
    [InlineData("0961234567")]    // Viettel 096
    [InlineData("0321234567")]    // Viettel 032
    [InlineData("0381234567")]    // Viettel 038
    [InlineData("0521234567")]    // Vietnamobile 052
    [InlineData("0561234567")]    // Vietnamobile 056
    [InlineData("0701234567")]    // Mobifone 070
    [InlineData("0791234567")]    // Mobifone 079
    [InlineData("0812345678")]    // Vinaphone 081
    [InlineData("0861234567")]    // Viettel 086
    [InlineData("0891234567")]    // Mobifone 089
    [InlineData("+84987654321")]  // International format with +84
    [InlineData("+84381234567")]  // International format with +84 (03x)
    [InlineData("+84701234567")]  // International format with +84 (07x)
    [InlineData("84987654321")]   // International format with 84
    [InlineData("84381234567")]   // International format with 84 (03x)
    public void Validator_Should_Not_Have_Error_When_Vietnamese_PhoneNumber_Is_Valid(string phoneNumber)
    {
        // Arrange
        var model = new CreateUserModel { PhoneNumber = phoneNumber };

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldNotHaveValidationErrorFor(u => u.PhoneNumber);
    }

    [Theory]
    [InlineData("0123456789")]    // Old 11-digit prefix 01 (invalid)
    [InlineData("0412345678")]    // Invalid prefix 04
    [InlineData("0612345678")]    // Invalid prefix 06
    [InlineData("0241234567")]    // Landline prefix 024 (invalid for mobile)
    [InlineData("123456789")]     // Missing leading 0 or 84
    [InlineData("0987654")]       // Too short
    [InlineData("098765432100")]  // Too long
    [InlineData("+84123456789")]  // Invalid network after +84
    [InlineData("+840987654321")] // Double zero after +84
    [InlineData("840987654321")]  // Zero after 84
    [InlineData("abcdefghij")]    // Letters
    [InlineData("098765432a")]    // Non-digit character
    public void Validator_Should_Have_Error_When_Vietnamese_PhoneNumber_Is_Invalid(string phoneNumber)
    {
        // Arrange
        var model = new CreateUserModel { PhoneNumber = phoneNumber };

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(u => u.PhoneNumber)
            .WithErrorMessage("Phone number is not a valid Vietnamese phone number");
    }
}
