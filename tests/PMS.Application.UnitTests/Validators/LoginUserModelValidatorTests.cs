using FluentValidation.TestHelper;
using PMS.Application.Models.User;
using PMS.Application.Models.Validators.User;
using Xunit;

namespace PMS.Application.UnitTests.Validators;

public class LoginUserModelValidatorTests
{
    private readonly LoginUserModelValidator _sut = new();

    [Fact]
    public void Validator_Should_Not_Have_Error_When_All_Fields_Are_Valid()
    {
        // Arrange
        var model = new LoginUserModel { Email = "validuser@example.com", Password = "Password.1!" };

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Validator_Should_Have_Error_When_Email_Is_Empty(string email)
    {
        // Arrange
        var model = new LoginUserModel { Email = email, Password = "Password.1!" };

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(u => u.Email);
    }

    [Fact]
    public void Validator_Should_Have_Error_When_Email_Is_Not_Valid()
    {
        // Arrange
        var model = new LoginUserModel { Email = "not-an-email", Password = "Password.1!" };

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(u => u.Email)
            .WithErrorMessage("Email address is not valid");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Validator_Should_Have_Error_When_Password_Is_Empty(string password)
    {
        // Arrange
        var model = new LoginUserModel { Email = "validuser@example.com", Password = password };

        // Act
        var result = _sut.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(u => u.Password);
    }
}
