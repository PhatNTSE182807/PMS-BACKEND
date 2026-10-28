using System.Net;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using PMS.Application.Common.Email;
using PMS.Application.Exceptions;
using PMS.Application.Models.User;
using PMS.DataAccess.Identity;

namespace PMS.Application.Services.Impl;

public class PasswordResetService(
    UserManager<ApplicationUser> userManager,
    IEmailService emailService,
    IConfiguration configuration) : IPasswordResetService
{
    public const string InvalidLinkMessage = "This reset link is invalid or has expired. Please request a new one.";

    /// <summary>Must match the reset link lifetime in <see cref="PasswordResetTokenProviderOptions"/>.</summary>
    private const int LinkLifetimeMinutes = 30;

    private const string DefaultFrontendBaseUrl = "http://localhost:5173";

    public async Task RequestResetAsync(ForgotPasswordModel model)
    {
        var user = await userManager.FindByEmailAsync(model.Email.Trim());

        // Unknown or deactivated accounts get no email, but the same answer as everyone else.
        // A temporary lockout (too many wrong passwords) does not block a reset: it is cleared on reset.
        if (user == null || !user.IsActive || string.IsNullOrEmpty(user.Email))
            return;

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var link = BuildResetLink(user.Email, token);

        // Accounts created by Google sign-in or by an email login code have no password yet:
        // tell them so, and let them set one with the same link (FR-AUTH-12).
        var hasPassword = await userManager.HasPasswordAsync(user);
        var message = hasPassword ? ResetEmail(user, link) : SetPasswordEmail(user, link);

        await emailService.SendEmailAsync(message);
    }

    public async Task ResetPasswordAsync(ResetPasswordModel model)
    {
        var user = await userManager.FindByEmailAsync(model.Email.Trim());
        if (user == null || !user.IsActive)
            throw new BadRequestException(InvalidLinkMessage);

        var token = DecodeToken(model.Token);
        if (token == null)
            throw new BadRequestException(InvalidLinkMessage);

        // Rotates the security stamp: this link and any older one stop working
        var result = await userManager.ResetPasswordAsync(user, token, model.NewPassword);
        if (!result.Succeeded)
        {
            var tokenRejected = result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken));
            throw new BadRequestException(tokenRejected
                ? InvalidLinkMessage
                : result.Errors.FirstOrDefault()?.Description ?? "Failed to reset the password.");
        }

        // The user proved they own the mailbox, so a lockout from wrong passwords no longer applies
        await userManager.ResetAccessFailedCountAsync(user);
        await userManager.SetLockoutEndDateAsync(user, null);
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await userManager.UpdateAsync(user);
        }

        await emailService.SendEmailAsync(PasswordChangedEmail(user));
    }

    private string BuildResetLink(string email, string token)
    {
        // The base URL comes from configuration, never from the request, so a link cannot point elsewhere
        var baseUrl = configuration["App:FrontendBaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = DefaultFrontendBaseUrl;

        // Identity tokens contain '+', '/' and '=': Base64Url keeps them intact in a query string
        var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        return $"{baseUrl.TrimEnd('/')}/reset-password?email={Uri.EscapeDataString(email)}&token={encodedToken}";
    }

    private static string DecodeToken(string encodedToken)
    {
        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(encodedToken.Trim()));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static EmailMessage ResetEmail(ApplicationUser user, string link)
    {
        var body = $"""
            <p>Hello {WebUtility.HtmlEncode(user.UserName)},</p>
            <p>We received a request to reset the password of your XXIV Parking account.</p>
            <p><a href="{WebUtility.HtmlEncode(link)}">Reset your password</a></p>
            <p>The link is valid for {LinkLifetimeMinutes} minutes and can be used only once.
            If you did not ask for it, you can ignore this email: your password stays the same.</p>
            """;
        return EmailMessage.Create(user.Email, body, "Reset your password");
    }

    private static EmailMessage SetPasswordEmail(ApplicationUser user, string link)
    {
        var body = $"""
            <p>Hello {WebUtility.HtmlEncode(user.UserName)},</p>
            <p>Your XXIV Parking account has no password: you sign in with <b>Sign in with Google</b>
            or with a login code sent to this email. You can keep doing that.</p>
            <p>If you also want to sign in with a password, set one here:</p>
            <p><a href="{WebUtility.HtmlEncode(link)}">Set a password</a></p>
            <p>The link is valid for {LinkLifetimeMinutes} minutes and can be used only once.
            If you did not ask for it, you can ignore this email.</p>
            """;
        return EmailMessage.Create(user.Email, body, "Set a password for your account");
    }

    private static EmailMessage PasswordChangedEmail(ApplicationUser user)
    {
        var body = $"""
            <p>Hello {WebUtility.HtmlEncode(user.UserName)},</p>
            <p>The password of your XXIV Parking account was just changed using a reset link.</p>
            <p>If this was not you, reset your password again right away and contact the parking lot staff.</p>
            """;
        return EmailMessage.Create(user.Email, body, "Your password was changed");
    }
}
