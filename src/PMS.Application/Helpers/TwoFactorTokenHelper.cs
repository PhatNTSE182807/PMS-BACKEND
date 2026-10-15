using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace PMS.Application.Helpers;

public static class TwoFactorTokenHelper
{
    public const string TwoFactorPurpose = "2FA_Challenge";

    public static string GenerateNumericOtp(int length = 6)
    {
        var min = (int)Math.Pow(10, length - 1);
        var max = (int)Math.Pow(10, length);
        return RandomNumberGenerator.GetInt32(min, max).ToString($"D{length}");
    }

    public static string HashOtpCode(string code, string salt, string secretKey)
    {
        using var sha256 = SHA256.Create();
        var raw = $"{secretKey}:{salt.Trim().ToLowerInvariant()}:{code.Trim()}";
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }

    public static string MaskEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return email ?? string.Empty;

        var parts = email.Split('@');
        var name = parts[0];
        var domain = parts[1];

        if (name.Length <= 2)
            return $"{name[0]}*@{domain}";

        return $"{name[0]}***{name[^1]}@{domain}";
    }

    private static byte[] GetTwoFactorSigningKey(IConfiguration configuration)
    {
        var secretKey = configuration.GetValue<string>("JwtConfiguration:SecretKey") ?? "DefaultTwoFactorSecretKeyForSigningChallenges";
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(Encoding.UTF8.GetBytes($"{secretKey}:PMS_2FA_CHALLENGE_PURPOSE"));
    }

    public static string GenerateTwoFactorToken(string userId, long otpId, string destination, IConfiguration configuration, TimeSpan? expiry = null)
    {
        var key = GetTwoFactorSigningKey(configuration);
        var tokenHandler = new JwtSecurityTokenHandler();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new("otp_id", otpId.ToString()),
            new("purpose", TwoFactorPurpose),
            new("dest", destination)
        };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.Add(expiry ?? TimeSpan.FromMinutes(5)),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    public static (bool isValid, string userId, long otpId, string destination, string error) ValidateTwoFactorToken(string token, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(token))
            return (false, null, 0, null, "Challenge token is required.");

        var key = GetTwoFactorSigningKey(configuration);
        var tokenHandler = new JwtSecurityTokenHandler();

        try
        {
            var principal = tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            }, out _);

            var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var otpIdStr = principal.FindFirst("otp_id")?.Value;
            var purpose = principal.FindFirst("purpose")?.Value;
            var dest = principal.FindFirst("dest")?.Value;

            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(otpIdStr) || purpose != TwoFactorPurpose)
                return (false, null, 0, null, "Invalid challenge token claims.");

            if (!long.TryParse(otpIdStr, out var otpId))
                return (false, null, 0, null, "Invalid challenge token format.");

            return (true, userId, otpId, dest, null);
        }
        catch (SecurityTokenExpiredException)
        {
            return (false, null, 0, null, "Verification challenge has expired. Please log in again.");
        }
        catch (Exception)
        {
            return (false, null, 0, null, "Invalid or tampered verification challenge.");
        }
    }
}
