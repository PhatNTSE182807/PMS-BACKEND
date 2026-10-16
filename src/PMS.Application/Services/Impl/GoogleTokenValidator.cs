using Google.Apis.Auth;
using Microsoft.Extensions.Configuration;
using PMS.Application.Exceptions;

namespace PMS.Application.Services.Impl;

public class GoogleTokenValidator(IConfiguration configuration) : IGoogleTokenValidator
{
    public async Task<GoogleIdentity> ValidateAsync(string idToken)
    {
        var clientId = configuration.GetValue<string>("GoogleAuth:ClientId");
        if (string.IsNullOrWhiteSpace(clientId))
            throw new BadRequestException("Google sign-in is not configured on the server.");

        GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await GoogleJsonWebSignature.ValidateAsync(idToken,
                new GoogleJsonWebSignature.ValidationSettings { Audience = [clientId] });
        }
        catch (InvalidJwtException)
        {
            throw new BadRequestException("Google sign-in failed. Please try again.");
        }

        return new GoogleIdentity(payload.Subject, payload.Email, payload.EmailVerified, payload.Name);
    }
}
