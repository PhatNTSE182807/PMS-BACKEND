using FluentValidation.TestHelper;
using PMS.Application.Models.Profile;
using PMS.Application.Models.Validators.Profile;
using Xunit;

namespace PMS.Application.UnitTests.Validators;

public class VehicleModelValidatorTests
{
    private readonly CreateVehicleModelValidator _createValidator = new();
    private readonly UpdateVehicleModelValidator _updateValidator = new();

    [Theory]
    [InlineData("51A-123.45")]   // car, 5-digit number
    [InlineData("30E-12345")]
    [InlineData("29A-1234")]     // car, old 4-digit number
    [InlineData("51LD-123.45")]  // car, two-letter series
    [InlineData("29-B1 123.45")] // motorcycle
    [InlineData("59-X1 1234")]
    [InlineData("29-AA 123.45")] // motorcycle under 50cc
    [InlineData("29-MĐ1 123.45")] // electric motorcycle
    [InlineData("51a-123.45")]   // lowercase input
    [InlineData(" 51A12345 ")]   // no separators
    public void Validator_Should_Accept_Valid_Vietnamese_Plates(string plate)
    {
        // Arrange
        var create = new CreateVehicleModel { LicensePlate = plate, VehicleTypeId = 1 };
        var update = new UpdateVehicleModel { LicensePlate = plate, VehicleTypeId = 1 };

        // Act & Assert
        _createValidator.TestValidate(create).ShouldNotHaveValidationErrorFor(v => v.LicensePlate);
        _updateValidator.TestValidate(update).ShouldNotHaveValidationErrorFor(v => v.LicensePlate);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("hello world")]
    [InlineData("123456")]        // digits only
    [InlineData("ABCDEF")]        // letters only
    [InlineData("00A-12345")]     // province code below 11
    [InlineData("05A-12345")]
    [InlineData("29-12345")]      // missing series letter
    [InlineData("29A-123")]       // number too short
    [InlineData("29A-1234567")]   // number too long
    [InlineData("29ABC-12345")]   // three-letter series
    [InlineData("A29-12345")]     // starts with a letter
    [InlineData("29A@12345")]     // invalid character
    [InlineData("29A--12345")]    // repeated separator
    public void Validator_Should_Reject_Invalid_Plates(string plate)
    {
        // Arrange
        var create = new CreateVehicleModel { LicensePlate = plate, VehicleTypeId = 1 };
        var update = new UpdateVehicleModel { LicensePlate = plate, VehicleTypeId = 1 };

        // Act & Assert
        _createValidator.TestValidate(create).ShouldHaveValidationErrorFor(v => v.LicensePlate);
        _updateValidator.TestValidate(update).ShouldHaveValidationErrorFor(v => v.LicensePlate);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Validator_Should_Require_License_Plate(string plate)
    {
        // Arrange
        var create = new CreateVehicleModel { LicensePlate = plate, VehicleTypeId = 1 };

        // Act & Assert
        _createValidator.TestValidate(create).ShouldHaveValidationErrorFor(v => v.LicensePlate)
            .WithErrorMessage("License plate is required.");
    }
}
