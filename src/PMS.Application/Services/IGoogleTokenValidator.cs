namespace PMS.Application.Services;

/// <summary>The account a Google ID token was issued for.</summary>
public record GoogleIdentity(string Subject, string Email, bool EmailVerified, string Name);

public interface IGoogleTokenValidator
{
    /// <summary>Checks the token's signature, issuer, expiry and audience (our client ID).</summary>
    Task<GoogleIdentity> ValidateAsync(string idToken);
}
