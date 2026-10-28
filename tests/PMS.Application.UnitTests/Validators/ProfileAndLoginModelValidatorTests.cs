using System.Collections.Generic;
using FluentValidation.TestHelper;
using PMS.Application.Models.Account;
using PMS.Application.Models.Profile;
using PMS.Application.Models.User;
using PMS.Application.Models.Validators.Account;
using PMS.Application.Models.Validators.Profile;
using PMS.Application.Models.Validators.User;
using Xunit;

namespace PMS.Application.UnitTests.Validators;

public class ProfileAndLoginModelValidatorTests
{
    // ---- Profile ----

    [Fact]
    public void UpdateProfile_Should_Accept_An_Empty_Model()
    {
        new UpdateUserProfileModelValidator().TestValidate(new UpdateUserProfileModel()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void UpdateProfile_Should_Accept_A_Full_Valid_Profile()
    {
        var model = new UpdateUserProfileModel
        {
            FullName = "Nguyen Van A",
            Email = "a.nguyen@example.com",
            PhoneNumber = "0912345678",
            AvatarUrl = "https://example.com/a.png",
            CitizenId = "012345678901"
        };

        new UpdateUserProfileModelValidator().TestValidate(model).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void UpdateProfile_Should_Reject_Invalid_Fields()
    {
        var model = new UpdateUserProfileModel
        {
            FullName = new string('a', 201),
            Email = "not-an-email",
            PhoneNumber = "12345",
            AvatarUrl = new string('a', 501),
            CitizenId = "123"
        };

        var result = new UpdateUserProfileModelValidator().TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.FullName);
        result.ShouldHaveValidationErrorFor(x => x.Email);
        result.ShouldHaveValidationErrorFor(x => x.PhoneNumber);
        result.ShouldHaveValidationErrorFor(x => x.AvatarUrl);
        result.ShouldHaveValidationErrorFor(x => x.CitizenId);
    }

    [Fact]
    public void ChangePassword_Should_Accept_A_Strong_New_Password()
    {
        var model = new ChangePasswordModel { CurrentPassword = "Old@123", NewPassword = "New@123", ConfirmPassword = "New@123" };

        new ChangePasswordModelValidator().TestValidate(model).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void ChangePassword_Should_Accept_A_First_Password_Without_Current_One()
    {
        var model = new ChangePasswordModel { NewPassword = "New@123", ConfirmPassword = "New@123" };

        new ChangePasswordModelValidator().TestValidate(model).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("A@1", "A@1")]           // too short
    [InlineData("abcdef1", "abcdef1")]   // no upper case or special character
    [InlineData("Same@123", "Same@123")] // same as the current password
    public void ChangePassword_Should_Reject_Weak_Or_Reused_Passwords(string password, string confirm)
    {
        var model = new ChangePasswordModel { CurrentPassword = "Same@123", NewPassword = password, ConfirmPassword = confirm };

        new ChangePasswordModelValidator().TestValidate(model).ShouldHaveValidationErrorFor(x => x.NewPassword);
    }

    [Fact]
    public void ChangePassword_Should_Reject_A_Mismatched_Confirmation()
    {
        var model = new ChangePasswordModel { NewPassword = "New@123", ConfirmPassword = "Other@123" };

        new ChangePasswordModelValidator().TestValidate(model).ShouldHaveValidationErrorFor(x => x.ConfirmPassword);
    }

    [Fact]
    public void ChangePassword_Should_Reject_A_Missing_Confirmation()
    {
        var model = new ChangePasswordModel { NewPassword = "New@123" };

        new ChangePasswordModelValidator().TestValidate(model).ShouldHaveValidationErrorFor(x => x.ConfirmPassword);
    }

    // ---- Login with a code / two-factor / Google ----

    [Fact]
    public void RequestLoginOtp_Should_Validate_The_Email()
    {
        var validator = new RequestLoginOtpModelValidator();

        validator.TestValidate(new RequestLoginOtpModel { Email = "a@example.com" }).ShouldNotHaveAnyValidationErrors();
        validator.TestValidate(new RequestLoginOtpModel { Email = "" }).ShouldHaveValidationErrorFor(x => x.Email);
        validator.TestValidate(new RequestLoginOtpModel { Email = "nope" }).ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void VerifyLoginOtp_Should_Validate_Email_And_Six_Digit_Code()
    {
        var validator = new VerifyLoginOtpModelValidator();

        validator.TestValidate(new VerifyLoginOtpModel { Email = "a@example.com", Code = "123456" })
            .ShouldNotHaveAnyValidationErrors();

        var result = validator.TestValidate(new VerifyLoginOtpModel { Email = "nope", Code = "12a" });
        result.ShouldHaveValidationErrorFor(x => x.Email);
        result.ShouldHaveValidationErrorFor(x => x.Code);

        validator.TestValidate(new VerifyLoginOtpModel()).ShouldHaveValidationErrorFor(x => x.Code);
    }

    [Theory]
    [InlineData("token", null)]
    [InlineData(null, "a@example.com")]
    public void TwoFactorLogin_Should_Accept_A_Token_Or_An_Email(string token, string email)
    {
        var model = new TwoFactorLoginModel { Code = "123456", TwoFactorToken = token, Email = email };

        new TwoFactorLoginModelValidator().TestValidate(model).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void TwoFactorLogin_Should_Reject_A_Bad_Code_And_No_Challenge()
    {
        var result = new TwoFactorLoginModelValidator().TestValidate(new TwoFactorLoginModel { Code = "12345" });

        result.ShouldHaveValidationErrorFor(x => x.Code);
        Assert.Contains(result.Errors, e => e.ErrorMessage.StartsWith("Either verification challenge token"));
    }

    [Fact]
    public void ResendTwoFactor_Should_Need_A_Token_Or_An_Email()
    {
        var validator = new ResendTwoFactorModelValidator();

        validator.TestValidate(new ResendTwoFactorModel { Email = "a@example.com" }).ShouldNotHaveAnyValidationErrors();
        Assert.False(validator.Validate(new ResendTwoFactorModel { TwoFactorToken = " " }).IsValid);
    }

    [Fact]
    public void GoogleLogin_Should_Need_A_Credential()
    {
        var validator = new GoogleLoginModelValidator();

        validator.TestValidate(new GoogleLoginModel { IdToken = "jwt" }).ShouldNotHaveAnyValidationErrors();
        validator.TestValidate(new GoogleLoginModel()).ShouldHaveValidationErrorFor(x => x.IdToken);
    }

    // ---- Lot assignment ----

    [Fact]
    public void AssignParkingLots_Should_Reject_Null_Or_Invalid_Ids()
    {
        var validator = new AssignParkingLotsModelValidator();

        validator.TestValidate(new AssignParkingLotsModel { ParkingLotIds = [1, 2] }).ShouldNotHaveAnyValidationErrors();
        validator.TestValidate(new AssignParkingLotsModel { ParkingLotIds = null }).ShouldHaveValidationErrorFor(x => x.ParkingLotIds);
        Assert.False(validator.Validate(new AssignParkingLotsModel { ParkingLotIds = new List<int> { 0 } }).IsValid);
    }

    [Fact]
    public void AssignStaffParkingLot_Should_Need_A_Lot()
    {
        var validator = new AssignStaffParkingLotModelValidator();

        validator.TestValidate(new AssignStaffParkingLotModel { ParkingLotId = 3 }).ShouldNotHaveAnyValidationErrors();
        validator.TestValidate(new AssignStaffParkingLotModel()).ShouldHaveValidationErrorFor(x => x.ParkingLotId);
    }
}
