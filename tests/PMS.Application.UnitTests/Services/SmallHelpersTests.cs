using System;
using System.Security.Claims;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using PMS.Application.Common.Email;
using PMS.Application.Helpers;
using PMS.Application.Models;
using PMS.Application.Services.Impl;
using PMS.Core.Exceptions;
using PMS.Shared.Services.Impl;
using Xunit;

namespace PMS.Application.UnitTests.Services;

/// <summary>Small shared helpers: SMTP settings, e-mail messages, API results, claims.</summary>
public class SmallHelpersTests
{
    private static SmtpSettings Smtp(string server = "smtp.example.com", string user = "mailer", string password = "secret") =>
        new() { Server = server, Port = 1, Username = user, Password = password, SenderName = "XXIV Parking", SenderEmail = "no-reply@example.com" };

    [Fact]
    public void SmtpSettings_Should_Be_Configured_With_Real_Values()
    {
        Smtp().IsConfigured.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "mailer", "secret")]
    [InlineData("smtp.example.com", " ", "secret")]
    [InlineData("smtp.example.com", "<username>", "secret")]
    [InlineData("smtp.example.com", "mailer", null)]
    [InlineData("smtp.example.com", "mailer", "<password>")]
    public void SmtpSettings_Should_Not_Be_Configured_With_Blanks_Or_Placeholders(string server, string user, string password)
    {
        Smtp(server, user, password).IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void EmailMessage_Should_Carry_No_One_Or_Many_Attachments()
    {
        var file = EmailAttachment.Create([1, 2, 3], "ticket.pdf");

        EmailMessage.Create("a@example.com", "<p>Hi</p>", "Hello").Attachments.Should().BeEmpty();
        EmailMessage.Create("a@example.com", "<p>Hi</p>", "Hello", [file, file]).Attachments.Should().HaveCount(2);

        var single = EmailMessage.Create("a@example.com", "<p>Hi</p>", "Hello", file);
        single.ToAddress.Should().Be("a@example.com");
        single.Subject.Should().Be("Hello");
        single.Body.Should().Be("<p>Hi</p>");
        single.Attachments.Should().ContainSingle().Which.Name.Should().Be("ticket.pdf");
        file.Value.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task EmailService_Should_Build_The_Message_And_Report_A_Failed_Connection()
    {
        // Port 1 on the local machine refuses the connection at once, so nothing is sent
        var service = new EmailService(Smtp(server: "127.0.0.1"));
        var message = EmailMessage.Create("a@example.com", "<p>Hi</p>", "Hello", EmailAttachment.Create([1], "a.txt"));

        await FluentActions.Invoking(() => service.SendEmailAsync(message)).Should().ThrowAsync<Exception>();
    }

    [Fact]
    public void ApiResult_Should_Hold_A_Result_Or_Errors()
    {
        var ok = ApiResult<int>.Success(42);
        var failed = ApiResult<int>.Failure(["Bad input"]);

        ok.Succeeded.Should().BeTrue();
        ok.Result.Should().Be(42);
        ok.Errors.Should().BeEmpty();
        failed.Succeeded.Should().BeFalse();
        failed.Result.Should().Be(0);
        failed.Errors.Should().Equal("Bad input");
    }

    [Fact]
    public void ClaimService_Should_Read_The_Signed_In_User()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "user-1"),
                new Claim(ClaimTypes.Role, "Staff")
            ], "test"))
        });
        var service = new ClaimService(accessor);

        service.GetUserId().Should().Be("user-1");
        service.GetClaim(ClaimTypes.Role).Should().Be("Staff");
        service.GetClaim(ClaimTypes.Email).Should().BeNull();
    }

    [Fact]
    public void ClaimService_Should_Return_Null_Outside_A_Request()
    {
        new ClaimService(Substitute.For<IHttpContextAccessor>()).GetUserId().Should().BeNull();
    }

    [Fact]
    public void RandomGenerator_Should_Stay_In_Range()
    {
        for (var i = 0; i < 20; i++)
            RandomGenerator.GenerateInteger(5, 8).Should().BeInRange(5, 7);
    }

    [Fact]
    public void ResourceNotFoundException_Should_Keep_Its_Message()
    {
        new ResourceNotFoundException().Message.Should().NotBeNull();
        new ResourceNotFoundException(typeof(string)).Message.Should().Be("System.String is missing");
        new ResourceNotFoundException("Gone").Message.Should().Be("Gone");
        var inner = new InvalidOperationException();
        new ResourceNotFoundException("Gone", inner).InnerException.Should().BeSameAs(inner);
    }
}
