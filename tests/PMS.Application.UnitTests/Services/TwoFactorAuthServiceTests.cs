using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MockQueryable;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PMS.Application.Common.Email;
using PMS.Application.Exceptions;
using PMS.Application.Helpers;
using PMS.Application.Models.User;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using Xunit;

namespace PMS.Application.UnitTests.Services;

public class TwoFactorAuthServiceTests : BaseServiceTestConfiguration
{
    private readonly PmsDbContext _pmsContext;
    private readonly IEmailService _emailService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserService _sut;

    public TwoFactorAuthServiceTests()
    {
        var dbOptions = new DbContextOptionsBuilder<PmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _pmsContext = new PmsDbContext(dbOptions);

        var userStore = Substitute.For<IUserStore<ApplicationUser>>();
        _userManager = Substitute.For<UserManager<ApplicationUser>>(userStore, null, null, null, null, null, null, null, null);
        var contextAccessor = Substitute.For<IHttpContextAccessor>();
        var userPrincipalFactory = Substitute.For<IUserClaimsPrincipalFactory<ApplicationUser>>();
        _signInManager = Substitute.For<SignInManager<ApplicationUser>>(_userManager, contextAccessor, userPrincipalFactory, null, null, null, null);
        _emailService = Substitute.For<IEmailService>();

        _sut = new UserService(Mapper, _userManager, _signInManager, Configuration, _pmsContext, _emailService);
    }

    [Fact]
    public async Task LoginAsync_WhenUserHas2FA_ValidPassword_Should_Initiate2FA_And_SendEmail_Without_Issuing_Token()
    {
        // Arrange
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "staff_2fa",
            Email = "staff@relaypark.vn",
            Role = "Staff",
            IsActive = true,
            EmailConfirmed = true,
            TwoFactorEnabled = true
        };

        var users = new List<ApplicationUser> { user }.AsQueryable().BuildMock();
        _userManager.Users.Returns(users);
        _signInManager.PasswordSignInAsync(user, "Password.1!", false, false)
            .Returns(SignInResult.TwoFactorRequired);

        EmailMessage capturedMessage = null;
        await _emailService.SendEmailAsync(Arg.Do<EmailMessage>(m => capturedMessage = m));

        // Act
        var result = await _sut.LoginAsync(new LoginUserModel
        {
            Username = "staff_2fa",
            Password = "Password.1!"
        });

        // Assert
        result.RequiresTwoFactor.Should().BeTrue();
        result.Token.Should().BeNull();
        result.TwoFactorToken.Should().NotBeNullOrEmpty();
        result.Destination.Should().Be(TwoFactorTokenHelper.MaskEmail(user.Email));
        capturedMessage.Should().NotBeNull();
        capturedMessage.ToAddress.Should().Be(user.Email);

        var otpInDb = await _pmsContext.OtpCodes.FirstOrDefaultAsync(o => o.UserId == user.Id);
        otpInDb.Should().NotBeNull();
        otpInDb.Purpose.Should().Be("TwoFactorLogin");
        otpInDb.ConsumedAt.Should().BeNull();
        otpInDb.AttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task LoginAsync_WhenEmailServiceFails_Should_ThrowException_And_Not_Issue_Token()
    {
        // Arrange
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "staff_email_fail",
            Email = "fail@relaypark.vn",
            Role = "Staff",
            IsActive = true,
            EmailConfirmed = true,
            TwoFactorEnabled = true
        };

        var users = new List<ApplicationUser> { user }.AsQueryable().BuildMock();
        _userManager.Users.Returns(users);
        _signInManager.PasswordSignInAsync(user, "Password.1!", false, false)
            .Returns(SignInResult.TwoFactorRequired);

        _emailService.SendEmailAsync(Arg.Any<EmailMessage>()).Throws(new Exception("SMTP connection failure"));

        // Act
        Func<Task> act = async () => await _sut.LoginAsync(new LoginUserModel
        {
            Username = "staff_email_fail",
            Password = "Password.1!"
        });

        // Assert
        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*Failed to send verification code*");

        var otpInDb = await _pmsContext.OtpCodes.FirstOrDefaultAsync(o => o.UserId == user.Id);
        otpInDb.Should().NotBeNull();
        otpInDb.ConsumedAt.Should().NotBeNull(); // Invalidated on email failure
    }

    [Fact]
    public async Task VerifyTwoFactorAsync_WithCorrectOtp_Should_Succeed_And_Return_Token()
    {
        // Arrange
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "staff_verify",
            Email = "verify@relaypark.vn",
            Role = "Staff",
            IsActive = true,
            EmailConfirmed = true,
            TwoFactorEnabled = true
        };

        var users = new List<ApplicationUser> { user }.AsQueryable().BuildMock();
        _userManager.Users.Returns(users);
        _userManager.FindByIdAsync(user.Id).Returns(user);
        _signInManager.PasswordSignInAsync(user, "Password.1!", false, false)
            .Returns(SignInResult.TwoFactorRequired);

        EmailMessage capturedMessage = null;
        await _emailService.SendEmailAsync(Arg.Do<EmailMessage>(m => capturedMessage = m));

        var loginResult = await _sut.LoginAsync(new LoginUserModel
        {
            Username = "staff_verify",
            Password = "Password.1!"
        });

        // Extract the code from the sent email
        var rawBody = capturedMessage.Body;
        var startTag = "<strong>";
        var endTag = "</strong>";
        var startIndex = rawBody.IndexOf(startTag) + startTag.Length;
        var endIndex = rawBody.IndexOf(endTag);
        var code = rawBody.Substring(startIndex, endIndex - startIndex);

        // Act
        var verifyResult = await _sut.VerifyTwoFactorAsync(new TwoFactorLoginModel
        {
            TwoFactorToken = loginResult.TwoFactorToken,
            Code = code
        });

        // Assert
        verifyResult.Should().NotBeNull();
        verifyResult.RequiresTwoFactor.Should().BeFalse();
        verifyResult.Token.Should().NotBeNullOrEmpty();
        verifyResult.Username.Should().Be(user.UserName);
        verifyResult.Email.Should().Be(user.Email);

        var otpInDb = await _pmsContext.OtpCodes.FirstOrDefaultAsync(o => o.UserId == user.Id);
        otpInDb.ConsumedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task VerifyTwoFactorAsync_WithIncorrectOtp_Should_Reject_And_IncrementAttemptCount()
    {
        // Arrange
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "staff_wrong",
            Email = "wrong@relaypark.vn",
            Role = "Staff",
            IsActive = true,
            EmailConfirmed = true,
            TwoFactorEnabled = true
        };

        var users = new List<ApplicationUser> { user }.AsQueryable().BuildMock();
        _userManager.Users.Returns(users);
        _signInManager.PasswordSignInAsync(user, "Password.1!", false, false)
            .Returns(SignInResult.TwoFactorRequired);

        var loginResult = await _sut.LoginAsync(new LoginUserModel
        {
            Username = "staff_wrong",
            Password = "Password.1!"
        });

        // Act
        Func<Task> act = async () => await _sut.VerifyTwoFactorAsync(new TwoFactorLoginModel
        {
            TwoFactorToken = loginResult.TwoFactorToken,
            Code = "000000"
        });

        // Assert
        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*Invalid verification code. 4 attempts remaining.*");

        var otpInDb = await _pmsContext.OtpCodes.FirstOrDefaultAsync(o => o.UserId == user.Id);
        otpInDb.AttemptCount.Should().Be(1);
        otpInDb.ConsumedAt.Should().BeNull();
    }

    [Fact]
    public async Task VerifyTwoFactorAsync_WhenMaxAttemptsExceeded_Should_InvalidateOtp()
    {
        // Arrange
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "staff_max_attempts",
            Email = "max@relaypark.vn",
            Role = "Staff",
            IsActive = true,
            EmailConfirmed = true,
            TwoFactorEnabled = true
        };

        var users = new List<ApplicationUser> { user }.AsQueryable().BuildMock();
        _userManager.Users.Returns(users);
        _signInManager.PasswordSignInAsync(user, "Password.1!", false, false)
            .Returns(SignInResult.TwoFactorRequired);

        var loginResult = await _sut.LoginAsync(new LoginUserModel
        {
            Username = "staff_max_attempts",
            Password = "Password.1!"
        });

        // 4 failed attempts
        for (int i = 0; i < 4; i++)
        {
            try
            {
                await _sut.VerifyTwoFactorAsync(new TwoFactorLoginModel
                {
                    TwoFactorToken = loginResult.TwoFactorToken,
                    Code = "000000"
                });
            }
            catch (BadRequestException) { }
        }

        // 5th attempt: should trigger max attempts lockout
        Func<Task> fifthAttempt = async () => await _sut.VerifyTwoFactorAsync(new TwoFactorLoginModel
        {
            TwoFactorToken = loginResult.TwoFactorToken,
            Code = "000000"
        });

        await fifthAttempt.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*Maximum verification attempts exceeded*");

        var otpInDb = await _pmsContext.OtpCodes.FirstOrDefaultAsync(o => o.UserId == user.Id);
        otpInDb.AttemptCount.Should().Be(5);
        otpInDb.ConsumedAt.Should().NotBeNull(); // Invalidated permanently
    }

    [Fact]
    public async Task VerifyTwoFactorAsync_WithExpiredOtp_Should_Reject()
    {
        // Arrange
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "staff_expired",
            Email = "expired@relaypark.vn",
            Role = "Staff",
            IsActive = true,
            EmailConfirmed = true,
            TwoFactorEnabled = true
        };

        var users = new List<ApplicationUser> { user }.AsQueryable().BuildMock();
        _userManager.Users.Returns(users);
        _signInManager.PasswordSignInAsync(user, "Password.1!", false, false)
            .Returns(SignInResult.TwoFactorRequired);

        var loginResult = await _sut.LoginAsync(new LoginUserModel
        {
            Username = "staff_expired",
            Password = "Password.1!"
        });

        // Fast-forward OTP expiration in DB
        var otpInDb = await _pmsContext.OtpCodes.FirstOrDefaultAsync(o => o.UserId == user.Id);
        otpInDb.ExpiresAt = DateTime.UtcNow.AddMinutes(-10);
        await _pmsContext.SaveChangesAsync();

        // Act
        Func<Task> act = async () => await _sut.VerifyTwoFactorAsync(new TwoFactorLoginModel
        {
            TwoFactorToken = loginResult.TwoFactorToken,
            Code = "123456"
        });

        // Assert
        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*Verification code has expired*");
    }

    [Fact]
    public async Task VerifyTwoFactorAsync_WhenOtpAlreadyConsumed_Should_PreventReplay()
    {
        // Arrange
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "staff_replay",
            Email = "replay@relaypark.vn",
            Role = "Staff",
            IsActive = true,
            EmailConfirmed = true,
            TwoFactorEnabled = true
        };

        var users = new List<ApplicationUser> { user }.AsQueryable().BuildMock();
        _userManager.Users.Returns(users);
        _userManager.FindByIdAsync(user.Id).Returns(user);
        _signInManager.PasswordSignInAsync(user, "Password.1!", false, false)
            .Returns(SignInResult.TwoFactorRequired);

        EmailMessage capturedMessage = null;
        await _emailService.SendEmailAsync(Arg.Do<EmailMessage>(m => capturedMessage = m));

        var loginResult = await _sut.LoginAsync(new LoginUserModel
        {
            Username = "staff_replay",
            Password = "Password.1!"
        });

        var rawBody = capturedMessage.Body;
        var code = rawBody.Substring(rawBody.IndexOf("<strong>") + 8, 6);

        // First verification: Success
        await _sut.VerifyTwoFactorAsync(new TwoFactorLoginModel
        {
            TwoFactorToken = loginResult.TwoFactorToken,
            Code = code
        });

        // Replay attempt with same challenge token and code
        Func<Task> replayAct = async () => await _sut.VerifyTwoFactorAsync(new TwoFactorLoginModel
        {
            TwoFactorToken = loginResult.TwoFactorToken,
            Code = code
        });

        await replayAct.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*already been used*");
    }

    [Fact]
    public async Task ResendTwoFactorOtpAsync_WithinCooldown_Should_RejectWithRateLimit()
    {
        // Arrange
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "staff_cooldown",
            Email = "cooldown@relaypark.vn",
            Role = "Staff",
            IsActive = true,
            EmailConfirmed = true,
            TwoFactorEnabled = true
        };

        var users = new List<ApplicationUser> { user }.AsQueryable().BuildMock();
        _userManager.Users.Returns(users);
        _userManager.FindByIdAsync(user.Id).Returns(user);
        _signInManager.PasswordSignInAsync(user, "Password.1!", false, false)
            .Returns(SignInResult.TwoFactorRequired);

        var loginResult = await _sut.LoginAsync(new LoginUserModel
        {
            Username = "staff_cooldown",
            Password = "Password.1!"
        });

        // Act: immediate resend attempt
        Func<Task> act = async () => await _sut.ResendTwoFactorOtpAsync(new ResendTwoFactorModel
        {
            TwoFactorToken = loginResult.TwoFactorToken
        });

        // Assert
        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*Please wait * seconds before requesting a new verification code*");
    }

    [Fact]
    public async Task ResendTwoFactorOtpAsync_AfterCooldown_Should_SendNewOtp_And_VoidPreviousOtp()
    {
        // Arrange
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "staff_resend_ok",
            Email = "resend_ok@relaypark.vn",
            Role = "Staff",
            IsActive = true,
            EmailConfirmed = true,
            TwoFactorEnabled = true
        };

        var users = new List<ApplicationUser> { user }.AsQueryable().BuildMock();
        _userManager.Users.Returns(users);
        _userManager.FindByIdAsync(user.Id).Returns(user);
        _signInManager.PasswordSignInAsync(user, "Password.1!", false, false)
            .Returns(SignInResult.TwoFactorRequired);

        var loginResult = await _sut.LoginAsync(new LoginUserModel
        {
            Username = "staff_resend_ok",
            Password = "Password.1!"
        });

        // Set previous OTP created 70 seconds ago (cooldown expired)
        var firstOtp = await _pmsContext.OtpCodes.FirstOrDefaultAsync(o => o.UserId == user.Id);
        firstOtp.CreatedAt = DateTime.UtcNow.AddSeconds(-70);
        await _pmsContext.SaveChangesAsync();

        // Act
        var resendResult = await _sut.ResendTwoFactorOtpAsync(new ResendTwoFactorModel
        {
            TwoFactorToken = loginResult.TwoFactorToken
        });

        // Assert
        resendResult.Succeeded.Should().BeTrue();
        resendResult.TwoFactorToken.Should().NotBeNullOrEmpty();
        resendResult.TwoFactorToken.Should().NotBe(loginResult.TwoFactorToken);

        // Previous OTP must be marked consumed
        firstOtp.ConsumedAt.Should().NotBeNull();

        // New OTP should exist in DB
        var otps = await _pmsContext.OtpCodes.Where(o => o.UserId == user.Id).ToListAsync();
        otps.Count.Should().Be(2);
        var activeOtp = otps.First(o => o.ConsumedAt == null);
        activeOtp.Id.Should().NotBe(firstOtp.Id);
    }

    [Fact]
    public async Task LoginAsync_WhenAccountIsInactive_Should_Reject_Before_Sending_Otp()
    {
        // Arrange
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "staff_deactivated",
            Email = "deactivated@relaypark.vn",
            Role = "Staff",
            IsActive = false,
            EmailConfirmed = true,
            TwoFactorEnabled = true
        };

        var users = new List<ApplicationUser> { user }.AsQueryable().BuildMock();
        _userManager.Users.Returns(users);

        // Act
        Func<Task> act = async () => await _sut.LoginAsync(new LoginUserModel
        {
            Username = "staff_deactivated",
            Password = "Password.1!"
        });

        // Assert
        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("Account is deactivated. Please contact an administrator.");

        await _emailService.DidNotReceive().SendEmailAsync(Arg.Any<EmailMessage>());
    }

    [Fact]
    public async Task LoginAsync_WhenAccountDeactivated_Should_Reject_Before_Sending_Otp()
    {
        // Arrange
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "staff_inactive",
            Email = "inactive@relaypark.vn",
            Role = "Staff",
            IsActive = false,
            EmailConfirmed = true,
            TwoFactorEnabled = true
        };

        var users = new List<ApplicationUser> { user }.AsQueryable().BuildMock();
        _userManager.Users.Returns(users);

        // Act
        Func<Task> act = async () => await _sut.LoginAsync(new LoginUserModel
        {
            Username = "staff_inactive",
            Password = "Password.1!"
        });

        // Assert
        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("Account is deactivated. Please contact an administrator.");

        await _emailService.DidNotReceive().SendEmailAsync(Arg.Any<EmailMessage>());
    }
}
