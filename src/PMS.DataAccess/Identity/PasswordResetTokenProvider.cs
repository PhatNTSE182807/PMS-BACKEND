using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PMS.DataAccess.Identity;

/// <summary>
/// Lifetime of the password reset link: 30 minutes (FR-AUTH-12).
/// Kept apart from <see cref="DataProtectionTokenProviderOptions"/> so other Identity tokens keep their own lifetime.
/// </summary>
public class PasswordResetTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public const string ProviderName = "PasswordReset";

    public PasswordResetTokenProviderOptions()
    {
        Name = "PasswordResetDataProtectorTokenProvider";
        TokenLifespan = TimeSpan.FromMinutes(30);
    }
}

/// <summary>
/// Issues the token sent in the password reset email. The token embeds the user's security stamp,
/// which <c>ResetPasswordAsync</c> rotates, so a link works only once and older links stop working.
/// </summary>
public class PasswordResetTokenProvider(
    IDataProtectionProvider dataProtectionProvider,
    IOptions<PasswordResetTokenProviderOptions> options,
    ILogger<DataProtectorTokenProvider<ApplicationUser>> logger)
    : DataProtectorTokenProvider<ApplicationUser>(dataProtectionProvider, options, logger);
