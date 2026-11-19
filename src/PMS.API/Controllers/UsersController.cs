using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PMS.Application.Models;
using PMS.Application.Models.Profile;
using PMS.Application.Models.User;
using PMS.Application.Services;
using PMS.Shared.Services;

namespace PMS.API.Controllers;

/// <summary>Swagger groups for the user endpoints (all served under /api/Users).</summary>
internal static class SwaggerTags
{
    public const string Authentication = "User Authentication";

    public const string Profile = "User Profile Management";
}

public class UsersController(
    IUserService userService,
    IUserProfileService userProfileService,
    IPasswordResetService passwordResetService,
    IClaimService claimService) : ApiController
{

    /// <summary>Creates a customer account.</summary>
    [HttpPost("register")]
    [Tags(SwaggerTags.Authentication)]
    [AllowAnonymous]
    public async Task<IActionResult> RegisterAsync(CreateUserModel createUserModel)
    {
        return Ok(ApiResult<CreateUserResponseModel>.Success(await userService.CreateAsync(createUserModel)));
    }

    /// <summary>Signs in with email and password; returns the tokens, or a two-factor challenge when it is enabled.</summary>
    [HttpPost("authenticate")]
    [Tags(SwaggerTags.Authentication)]
    [AllowAnonymous]
    public async Task<IActionResult> LoginAsync(LoginUserModel loginUserModel)
    {
        return Ok(ApiResult<LoginResponseModel>.Success(await userService.LoginAsync(loginUserModel)));
    }

    /// <summary>Completes a sign-in with the two-factor code sent by email.</summary>
    [HttpPost("two-factor")]
    [Tags(SwaggerTags.Authentication)]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyTwoFactorAsync([FromBody] TwoFactorLoginModel model)
    {
        return Ok(ApiResult<LoginResponseModel>.Success(await userService.VerifyTwoFactorAsync(model)));
    }

    /// <summary>Sends a new two-factor code for a pending sign-in.</summary>
    [HttpPost("two-factor/resend")]
    [Tags(SwaggerTags.Authentication)]
    [AllowAnonymous]
    public async Task<IActionResult> ResendTwoFactorOtpAsync([FromBody] ResendTwoFactorModel model)
    {
        return Ok(ApiResult<ResendTwoFactorResponseModel>.Success(await userService.ResendTwoFactorOtpAsync(model)));
    }

    /// <summary>Sends a one-time sign-in code to the email address.</summary>
    [HttpPost("login-otp/request")]
    [Tags(SwaggerTags.Authentication)]
    [AllowAnonymous]
    public async Task<IActionResult> RequestLoginOtpAsync(RequestLoginOtpModel model)
    {
        await userService.RequestLoginOtpAsync(model);
        return Ok(ApiResult<string>.Success("A login code has been sent to your email."));
    }

    /// <summary>Signs in with the one-time code sent by email.</summary>
    [HttpPost("login-otp/verify")]
    [Tags(SwaggerTags.Authentication)]
    [AllowAnonymous]
    public async Task<IActionResult> LoginWithOtpAsync(VerifyLoginOtpModel model)
    {
        return Ok(ApiResult<LoginResponseModel>.Success(await userService.LoginWithOtpAsync(model)));
    }

    /// <summary>Emails a password reset link; the answer is the same whether or not the email has an account.</summary>
    [HttpPost("forgot-password")]
    [Tags(SwaggerTags.Authentication)]
    [AllowAnonymous]
    [EnableRateLimiting(ApiDependencyInjection.PasswordResetRateLimit)]
    public async Task<IActionResult> ForgotPasswordAsync([FromBody] ForgotPasswordModel model)
    {
        await passwordResetService.RequestResetAsync(model);
        // Same answer whether or not the email has an account
        return Ok(ApiResult<string>.Success(
            "If an account uses this email, we have sent a link to reset the password. Please check your inbox."));
    }

    /// <summary>Sets a new password with the token from the reset link.</summary>
    [HttpPost("reset-password")]
    [Tags(SwaggerTags.Authentication)]
    [AllowAnonymous]
    [EnableRateLimiting(ApiDependencyInjection.PasswordResetRateLimit)]
    public async Task<IActionResult> ResetPasswordAsync([FromBody] ResetPasswordModel model)
    {
        await passwordResetService.ResetPasswordAsync(model);
        return Ok(ApiResult<string>.Success("Your password has been reset. You can now sign in with it."));
    }

    /// <summary>Signs in (or signs up) with a Google ID token.</summary>
    [HttpPost("login-google")]
    [Tags(SwaggerTags.Authentication)]
    [AllowAnonymous]
    public async Task<IActionResult> LoginWithGoogleAsync(GoogleLoginModel model)
    {
        return Ok(ApiResult<LoginResponseModel>.Success(await userService.LoginWithGoogleAsync(model)));
    }

    /// <summary>The signed-in user's profile.</summary>
    [HttpGet("profile")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> GetProfileAsync()
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<UserProfileResponseModel>.Success(await userProfileService.GetProfileAsync(userId)));
    }

    /// <summary>Updates the signed-in user's profile.</summary>
    [HttpPut("profile")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> UpdateProfileAsync([FromBody] UpdateUserProfileModel model)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<UserProfileResponseModel>.Success(await userProfileService.UpdateProfileAsync(userId, model)));
    }

    /// <summary>Checks whether a phone number or citizen ID is already used by another account.</summary>
    [HttpGet("profile/check-unique")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> CheckProfileUniquenessAsync([FromQuery] string? phone = null, [FromQuery] string? citizenId = null)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<ProfileFieldAvailabilityModel>.Success(await userProfileService.CheckProfileUniquenessAsync(userId, phone, citizenId)));
    }

    /// <summary>Uploads a new profile picture for the signed-in user.</summary>
    [HttpPost("profile/avatar")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> UploadAvatarAsync(IFormFile file)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<UserProfileResponseModel>.Success(await userProfileService.UploadAvatarAsync(userId, file)));
    }

    /// <summary>
    /// Serves a profile photo. Public because an image tag cannot send the sign-in token; the id is a random
    /// GUID that changes on every upload, so the response never changes and can be cached for a long time.
    /// </summary>
    [HttpGet("avatars/{avatarId:guid}")]
    [Tags(SwaggerTags.Profile)]
    [AllowAnonymous]
    [ResponseCache(Duration = 31536000, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> GetAvatarAsync(Guid avatarId)
    {
        var avatar = await userProfileService.GetAvatarAsync(avatarId);
        if (avatar == null)
            return NotFound();

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(avatar.Content, avatar.ContentType);
    }

    /// <summary>The signed-in user's vehicles.</summary>
    [HttpGet("profile/vehicles")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> GetVehiclesAsync()
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<List<VehicleResponseModel>>.Success(await userProfileService.GetVehiclesAsync(userId)));
    }

    /// <summary>Checks whether a plate can be added (not already registered by another account).</summary>
    [HttpGet("profile/vehicles/check-plate")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> CheckPlateAvailabilityAsync([FromQuery] string plate, [FromQuery] Guid? excludeVehicleId = null)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<PlateAvailabilityResponseModel>.Success(await userProfileService.CheckPlateAvailabilityAsync(userId, plate, excludeVehicleId)));
    }

    /// <summary>Adds a vehicle to the signed-in user's account.</summary>
    [HttpPost("profile/vehicles")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> AddVehicleAsync([FromBody] CreateVehicleModel model)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<VehicleResponseModel>.Success(await userProfileService.AddVehicleAsync(userId, model)));
    }

    /// <summary>Updates one of the signed-in user's vehicles.</summary>
    [HttpPut("profile/vehicles/{vehicleId:guid}")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> UpdateVehicleAsync(Guid vehicleId, [FromBody] UpdateVehicleModel model)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<VehicleResponseModel>.Success(await userProfileService.UpdateVehicleAsync(userId, vehicleId, model)));
    }

    /// <summary>Removes one of the signed-in user's vehicles.</summary>
    [HttpDelete("profile/vehicles/{vehicleId:guid}")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> DeleteVehicleAsync(Guid vehicleId)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<bool>.Success(await userProfileService.DeleteVehicleAsync(userId, vehicleId)));
    }

    /// <summary>Makes a vehicle the default one for bookings.</summary>
    [HttpPatch("profile/vehicles/{vehicleId:guid}/default")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> SetDefaultVehicleAsync(Guid vehicleId)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<VehicleResponseModel>.Success(await userProfileService.SetDefaultVehicleAsync(userId, vehicleId)));
    }

    /// <summary>Vehicle types a customer can register.</summary>
    [HttpGet("profile/vehicle-types")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> GetVehicleTypesAsync()
    {
        return Ok(ApiResult<List<VehicleTypeResponseModel>>.Success(await userProfileService.GetVehicleTypesAsync()));
    }

    /// <summary>Changes the signed-in user's password (the current password is required).</summary>
    [HttpPut("profile/change-password")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> ChangePasswordAsync([FromBody] ChangePasswordModel model)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<bool>.Success(await userProfileService.ChangePasswordAsync(userId, model)));
    }
}
