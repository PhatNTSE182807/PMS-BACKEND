using System.Threading.Tasks;
using PMS.Application.Exceptions;
using PMS.Application.Services;

namespace PMS.Api.IntegrationTests.Helpers.Services;

/// <summary>
/// Stands in for Google: a test "ID token" is "email|verified|name", e.g. "an@gmail.com|true|An Nguyen".
/// Anything else is rejected like a forged or expired token.
/// </summary>
public class GoogleTokenTestValidator : IGoogleTokenValidator
{
    public static string Token(string email, bool verified = true, string name = "Google User") =>
        $"{email}|{verified.ToString().ToLowerInvariant()}|{name}";

    public Task<GoogleIdentity> ValidateAsync(string idToken)
    {
        var parts = idToken.Split('|');
        if (parts.Length != 3)
            throw new BadRequestException("Google sign-in failed. Please try again.");

        return Task.FromResult(new GoogleIdentity($"sub-{parts[0]}", parts[0], parts[1] == "true", parts[2]));
    }
}
