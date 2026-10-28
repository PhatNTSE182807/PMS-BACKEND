using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using NSubstitute;
using PMS.Application.Common.Email;
using PMS.Application.Exceptions;
using PMS.Application.Models.User;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Identity;
using Xunit;

namespace PMS.Application.UnitTests.Services;

public class PasswordResetServiceTests : BaseServiceTestConfiguration
{
    private const string Email = "user@example.com";

    // Base64Url of "raw-token"
    private const string EncodedToken = "cmF3LXRva2Vu";

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailService _emailService;
    private readonly PasswordResetService _sut;

    public PasswordResetServiceTests()
    {
        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        _userManager =
            Substitute.For<UserManager<ApplicationUser>>(userStore, null, null, null, null, null, null, null, null);
        _emailService = Substitute.For<IEmailService>();
        _sut = new PasswordResetService(_userManager, _emailService, Configuration);
    }

    private ApplicationUser GivenUser(bool isActive = true, bool hasPassword = true)
    {
        var user = new ApplicationUser { UserName = "user", Email = Email, IsActive = isActive, Role = "User" };
        _userManager.FindByEmailAsync(Email).Returns(user);
        _userManager.HasPasswordAsync(user).Returns(hasPassword);
        _userManager.GeneratePasswordResetTokenAsync(user).Returns("token+with/chars=");
        return user;
    }

    private static ResetPasswordModel Reset(string token = EncodedToken) => new()
    {
        Email = Email,
        Token = token,
        NewPassword = "NewPass!1",
        ConfirmPassword = "NewPass!1"
    };

    [Fact]
    public async Task RequestResetAsync_Should_Not_Send_Email_For_Unknown_Address()
    {
        // Act
        await _sut.RequestResetAsync(new ForgotPasswordModel { Email = "nobody@example.com" });

        // Assert
        await _emailService.DidNotReceiveWithAnyArgs().SendEmailAsync(default!);
    }

    [Fact]
    public async Task RequestResetAsync_Should_Not_Send_Email_For_Deactivated_Account()
    {
        // Arrange
        GivenUser(isActive: false);

        // Act
        await _sut.RequestResetAsync(new ForgotPasswordModel { Email = Email });

        // Assert
        await _emailService.DidNotReceiveWithAnyArgs().SendEmailAsync(default!);
    }

    [Fact]
    public async Task RequestResetAsync_Should_Send_Reset_Link_With_Url_Safe_Token()
    {
        // Arrange
        GivenUser();

        // Act
        await _sut.RequestResetAsync(new ForgotPasswordModel { Email = $"  {Email} " });

        // Assert
        await _emailService.Received(1).SendEmailAsync(Arg.Is<EmailMessage>(m =>
            m.ToAddress == Email &&
            m.Subject == "Reset your password" &&
            m.Body.Contains("/reset-password?email=user%40example.com&amp;token=") &&
            !m.Body.Contains("token+with/chars=")));
    }

    [Fact]
    public async Task RequestResetAsync_Should_Offer_To_Set_A_Password_When_Account_Has_None()
    {
        // Arrange: created by Google sign-in or an email login code
        GivenUser(hasPassword: false);

        // Act
        await _sut.RequestResetAsync(new ForgotPasswordModel { Email = Email });

        // Assert
        await _emailService.Received(1).SendEmailAsync(Arg.Is<EmailMessage>(m =>
            m.Subject == "Set a password for your account" && m.Body.Contains("Sign in with Google")));
    }

    [Fact]
    public async Task ResetPasswordAsync_Should_Reject_Deactivated_Account()
    {
        // Arrange
        GivenUser(isActive: false);

        // Act
        var act = () => _sut.ResetPasswordAsync(Reset());

        // Assert
        await act.Should().ThrowAsync<BadRequestException>().WithMessage(PasswordResetService.InvalidLinkMessage);
        await _userManager.DidNotReceiveWithAnyArgs().ResetPasswordAsync(default!, default!, default!);
    }

    [Fact]
    public async Task ResetPasswordAsync_Should_Reject_Token_That_Is_Not_Base64Url()
    {
        // Arrange
        GivenUser();

        // Act
        var act = () => _sut.ResetPasswordAsync(Reset("%%%"));

        // Assert
        await act.Should().ThrowAsync<BadRequestException>().WithMessage(PasswordResetService.InvalidLinkMessage);
    }

    [Fact]
    public async Task ResetPasswordAsync_Should_Report_Expired_Or_Used_Link()
    {
        // Arrange
        var user = GivenUser();
        _userManager.ResetPasswordAsync(user, "raw-token", "NewPass!1")
            .Returns(IdentityResult.Failed(new IdentityError { Code = "InvalidToken", Description = "Invalid token." }));

        // Act
        var act = () => _sut.ResetPasswordAsync(Reset());

        // Assert
        await act.Should().ThrowAsync<BadRequestException>().WithMessage(PasswordResetService.InvalidLinkMessage);
        await _emailService.DidNotReceiveWithAnyArgs().SendEmailAsync(default!);
    }

    [Fact]
    public async Task ResetPasswordAsync_Should_Clear_Lockout_Confirm_Email_And_Notify()
    {
        // Arrange
        var user = GivenUser();
        user.EmailConfirmed = false;
        _userManager.ResetPasswordAsync(user, "raw-token", "NewPass!1").Returns(IdentityResult.Success);

        // Act
        await _sut.ResetPasswordAsync(Reset());

        // Assert
        await _userManager.Received(1).ResetAccessFailedCountAsync(user);
        await _userManager.Received(1).SetLockoutEndDateAsync(user, null);
        user.EmailConfirmed.Should().BeTrue();
        await _emailService.Received(1).SendEmailAsync(Arg.Is<EmailMessage>(m => m.Subject == "Your password was changed"));
    }
}
