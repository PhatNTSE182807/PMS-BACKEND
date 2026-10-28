using FluentValidation.TestHelper;
using PMS.Application.Models.User;
using PMS.Application.Models.Validators.User;
using Xunit;

namespace PMS.Application.UnitTests.Validators;

public class PasswordResetModelValidatorTests
{
    private readonly ForgotPasswordModelValidator _forgot = new();
    private readonly ResetPasswordModelValidator _reset = new();

    private static ResetPasswordModel ValidReset() => new()
    {
        Email = "user@example.com",
        Token = "Q2ZESjhQ",
        NewPassword = "NewPass!1",
        ConfirmPassword = "NewPass!1"
    };

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-an-email")]
    public void Forgot_Should_Have_Error_When_Email_Is_Missing_Or_Invalid(string email)
    {
        // Act
        var result = _forgot.TestValidate(new ForgotPasswordModel { Email = email });

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void Reset_Should_Not_Have_Error_When_All_Fields_Are_Valid()
    {
        // Act
        var result = _reset.TestValidate(ValidReset());

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("Ab!1")]         // too short
    [InlineData("password!1")]   // no upper case letter
    [InlineData("Password1")]    // no special character
    public void Reset_Should_Have_Error_When_Password_Breaks_The_Team_Rule(string password)
    {
        // Arrange
        var model = ValidReset();
        model.NewPassword = password;
        model.ConfirmPassword = password;

        // Act
        var result = _reset.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.NewPassword);
    }

    [Fact]
    public void Reset_Should_Have_Error_When_Confirmation_Does_Not_Match()
    {
        // Arrange
        var model = ValidReset();
        model.ConfirmPassword = "Other!Pass1";

        // Act
        var result = _reset.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.ConfirmPassword);
    }

    [Fact]
    public void Reset_Should_Have_Error_When_Token_Is_Missing()
    {
        // Arrange
        var model = ValidReset();
        model.Token = "";

        // Act
        var result = _reset.TestValidate(model);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Token);
    }
}
