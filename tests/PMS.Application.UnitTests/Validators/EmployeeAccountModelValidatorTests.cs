using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using NSubstitute;
using PMS.Application.Models.Account;
using PMS.Application.Models.Validators.Account;
using PMS.DataAccess.Identity;
using Xunit;

namespace PMS.Application.UnitTests.Validators;

public class EmployeeAccountModelValidatorTests
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly CreateStaffAccountModelValidator _createValidator;
    private readonly UpdateEmployeeAccountModelValidator _updateValidator = new();

    public EmployeeAccountModelValidatorTests()
    {
        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        _userManager = Substitute.For<UserManager<ApplicationUser>>(userStore, null, null, null, null, null, null, null, null);
        _createValidator = new CreateStaffAccountModelValidator(_userManager);
    }

    private static CreateStaffAccountModel ValidStaff() => new()
    {
        FullName = "Nguyen Van A",
        Email = "nva@test.com",
        PhoneNumber = "0912345678",
        Password = "Password1!",
        ConfirmPassword = "Password1!",
        ParkingLotId = 1
    };

    [Fact]
    public void Create_Should_Accept_A_Valid_Model()
    {
        _createValidator.Validate(ValidStaff()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("Abcde!")]
    [InlineData("PASS@WORD")]
    public void Create_Should_Accept_Six_Characters_With_An_Upper_Case_Letter_And_A_Special_Character(string password)
    {
        var model = ValidStaff();
        model.Password = password;
        model.ConfirmPassword = password;

        _createValidator.Validate(model).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("password1!")]
    [InlineData("Password1")]
    [InlineData("Ab1!")]
    [InlineData("Abcd!")]
    public void Create_Should_Reject_A_Weak_Password(string password)
    {
        var model = ValidStaff();
        model.Password = password;
        model.ConfirmPassword = password;

        _createValidator.Validate(model).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Create_Should_Reject_Mismatched_Confirmation()
    {
        var model = ValidStaff();
        model.ConfirmPassword = "Password2";

        _createValidator.Validate(model).Errors.Should().Contain(e => e.PropertyName == nameof(CreateStaffAccountModel.ConfirmPassword));
    }

    [Fact]
    public void Create_Should_Reject_A_Taken_Email()
    {
        _userManager.FindByEmailAsync("nva@test.com").Returns(new ApplicationUser());

        _createValidator.Validate(ValidStaff()).Errors.Should().Contain(e => e.PropertyName == nameof(CreateStaffAccountModel.Email));
    }

    [Fact]
    public void Create_Should_Require_A_Parking_Lot()
    {
        var model = ValidStaff();
        model.ParkingLotId = 0;

        _createValidator.Validate(model).Errors.Should().Contain(e => e.PropertyName == nameof(CreateStaffAccountModel.ParkingLotId));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("")]
    public void Update_Should_Reject_A_Bad_Full_Name(string fullName)
    {
        var model = new UpdateEmployeeAccountModel { FullName = fullName, Email = "a@test.com", PhoneNumber = "0912345678" };

        _updateValidator.Validate(model).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("912345678")]
    [InlineData("+84912345678")]
    [InlineData("09123456789")]
    public void Update_Should_Reject_A_Phone_That_Is_Not_10_Digits_From_0(string phone)
    {
        var model = new UpdateEmployeeAccountModel { FullName = "Nguyen Van A", Email = "a@test.com", PhoneNumber = phone };

        _updateValidator.Validate(model).IsValid.Should().BeFalse();
    }
}
