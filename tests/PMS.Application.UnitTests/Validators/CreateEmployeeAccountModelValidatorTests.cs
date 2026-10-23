using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using NSubstitute;
using PMS.Application.Models.Admin;
using PMS.Application.Models.Validators.Admin;
using PMS.DataAccess.Identity;
using Xunit;

namespace PMS.Application.UnitTests.Validators;

public class CreateEmployeeAccountModelValidatorTests
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly CreateEmployeeAccountModelValidator _validator;

    public CreateEmployeeAccountModelValidatorTests()
    {
        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        _userManager = Substitute.For<UserManager<ApplicationUser>>(userStore, null, null, null, null, null, null, null, null);
        _validator = new CreateEmployeeAccountModelValidator(_userManager);
    }

    [Fact]
    public async Task Validate_WhenModelIsValid_ShouldPass()
    {
        var model = new CreateEmployeeAccountModel
        {
            UserName = "validmanager",
            FullName = "Nguyen Van A",
            Email = "manager@example.com",
            Password = "Password123!",
            PhoneNumber = "0912345678"
        };

        var result = await _validator.ValidateAsync(model);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("a_very_long_username_exceeding_twenty_chars")]
    public async Task Validate_WhenUsernameIsInvalid_ShouldFail(string username)
    {
        var model = new CreateEmployeeAccountModel
        {
            UserName = username,
            FullName = "Nguyen Van A",
            Email = "manager@example.com",
            Password = "Password123!",
            PhoneNumber = "0912345678"
        };

        var result = await _validator.ValidateAsync(model);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(model.UserName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid-email")]
    [InlineData("user@")]
    public async Task Validate_WhenEmailIsInvalid_ShouldFail(string email)
    {
        var model = new CreateEmployeeAccountModel
        {
            UserName = "validmanager",
            FullName = "Nguyen Van A",
            Email = email,
            Password = "Password123!",
            PhoneNumber = "0912345678"
        };

        var result = await _validator.ValidateAsync(model);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(model.Email));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("abcdefghij")]
    public async Task Validate_WhenPhoneNumberIsInvalid_ShouldFail(string phone)
    {
        var model = new CreateEmployeeAccountModel
        {
            UserName = "validmanager",
            FullName = "Nguyen Van A",
            Email = "manager@example.com",
            Password = "Password123!",
            PhoneNumber = phone
        };

        var result = await _validator.ValidateAsync(model);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(model.PhoneNumber));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    public async Task Validate_WhenPasswordIsTooShort_ShouldFail(string password)
    {
        var model = new CreateEmployeeAccountModel
        {
            UserName = "validmanager",
            FullName = "Nguyen Van A",
            Email = "manager@example.com",
            Password = password,
            PhoneNumber = "0912345678"
        };

        var result = await _validator.ValidateAsync(model);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(model.Password));
    }

    [Fact]
    public async Task Validate_WhenUsernameAlreadyExists_ShouldFail()
    {
        _userManager.FindByNameAsync("existinguser")
            .Returns(Task.FromResult(new ApplicationUser { UserName = "existinguser" }));

        var model = new CreateEmployeeAccountModel
        {
            UserName = "existinguser",
            FullName = "Nguyen Van A",
            Email = "manager@example.com",
            Password = "Password123!",
            PhoneNumber = "0912345678"
        };

        var result = await _validator.ValidateAsync(model);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Username is not available");
    }

    [Fact]
    public async Task Validate_WhenEmailAlreadyExists_ShouldFail()
    {
        _userManager.FindByEmailAsync("existing@example.com")
            .Returns(Task.FromResult(new ApplicationUser { Email = "existing@example.com" }));

        var model = new CreateEmployeeAccountModel
        {
            UserName = "newuser",
            FullName = "Nguyen Van A",
            Email = "existing@example.com",
            Password = "Password123!",
            PhoneNumber = "0912345678"
        };

        var result = await _validator.ValidateAsync(model);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Email address is already in use");
    }
}
