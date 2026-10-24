using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    public const string Management = "User Management";
}

public class UsersController(
    IUserService userService,
    IUserProfileService userProfileService,
    IClaimService claimService) : ApiController
{
    [HttpGet]
    [Tags(SwaggerTags.Management)]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAllAsync([FromQuery] string role = null, [FromQuery] bool? isActive = null, [FromQuery] string searchTerm = null)
    {
        return Ok(ApiResult<IEnumerable<UserResponseModel>>.Success(await userService.GetAllAsync(role, isActive, searchTerm)));
    }

    [HttpGet("{id}")]
    [Tags(SwaggerTags.Management)]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetByIdAsync(string id)
    {
        return Ok(ApiResult<UserResponseModel>.Success(await userService.GetByIdAsync(id)));
    }

    [HttpPost("internal")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateInternalUserAsync([FromBody] CreateInternalUserModel model)
    {
        return Ok(ApiResult<CreateUserResponseModel>.Success(await userService.CreateInternalUserAsync(model)));
    }

    [HttpPatch("{id}/status")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateStatusAsync(string id, [FromBody] UpdateUserStatusModel model)
    {
        var currentUserId = claimService.GetUserId();
        return Ok(ApiResult<bool>.Success(await userService.UpdateUserStatusAsync(currentUserId, id, model.IsActive)));
    }

    [HttpPost("register")]
    [Tags(SwaggerTags.Authentication)]
    [AllowAnonymous]
    public async Task<IActionResult> RegisterAsync(CreateUserModel createUserModel)
    {
        return Ok(ApiResult<CreateUserResponseModel>.Success(await userService.CreateAsync(createUserModel)));
    }

    [HttpPost("authenticate")]
    [Tags(SwaggerTags.Authentication)]
    [AllowAnonymous]
    public async Task<IActionResult> LoginAsync(LoginUserModel loginUserModel)
    {
        return Ok(ApiResult<LoginResponseModel>.Success(await userService.LoginAsync(loginUserModel)));
    }

    [HttpPost("two-factor")]
    [Tags(SwaggerTags.Authentication)]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyTwoFactorAsync([FromBody] TwoFactorLoginModel model)
    {
        return Ok(ApiResult<LoginResponseModel>.Success(await userService.VerifyTwoFactorAsync(model)));
    }

    [HttpPost("two-factor/resend")]
    [Tags(SwaggerTags.Authentication)]
    [AllowAnonymous]
    public async Task<IActionResult> ResendTwoFactorOtpAsync([FromBody] ResendTwoFactorModel model)
    {
        return Ok(ApiResult<ResendTwoFactorResponseModel>.Success(await userService.ResendTwoFactorOtpAsync(model)));
    }

    [HttpPost("login-otp/request")]
    [Tags(SwaggerTags.Authentication)]
    [AllowAnonymous]
    public async Task<IActionResult> RequestLoginOtpAsync(RequestLoginOtpModel model)
    {
        await userService.RequestLoginOtpAsync(model);
        return Ok(ApiResult<string>.Success("A login code has been sent to your email."));
    }

    [HttpPost("login-otp/verify")]
    [Tags(SwaggerTags.Authentication)]
    [AllowAnonymous]
    public async Task<IActionResult> LoginWithOtpAsync(VerifyLoginOtpModel model)
    {
        return Ok(ApiResult<LoginResponseModel>.Success(await userService.LoginWithOtpAsync(model)));
    }

    [HttpPost("login-google")]
    [Tags(SwaggerTags.Authentication)]
    [AllowAnonymous]
    public async Task<IActionResult> LoginWithGoogleAsync(GoogleLoginModel model)
    {
        return Ok(ApiResult<LoginResponseModel>.Success(await userService.LoginWithGoogleAsync(model)));
    }

    [HttpGet("profile")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> GetProfileAsync()
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<UserProfileResponseModel>.Success(await userProfileService.GetProfileAsync(userId)));
    }

    [HttpPut("profile")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> UpdateProfileAsync([FromBody] UpdateUserProfileModel model)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<UserProfileResponseModel>.Success(await userProfileService.UpdateProfileAsync(userId, model)));
    }

    [HttpGet("profile/check-unique")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> CheckProfileUniquenessAsync([FromQuery] string? phone = null, [FromQuery] string? citizenId = null)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<ProfileFieldAvailabilityModel>.Success(await userProfileService.CheckProfileUniquenessAsync(userId, phone, citizenId)));
    }

    [HttpPost("profile/avatar")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> UploadAvatarAsync(IFormFile file)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<UserProfileResponseModel>.Success(await userProfileService.UploadAvatarAsync(userId, file)));
    }

    [HttpGet("profile/vehicles")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> GetVehiclesAsync()
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<List<VehicleResponseModel>>.Success(await userProfileService.GetVehiclesAsync(userId)));
    }

    [HttpGet("profile/vehicles/check-plate")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> CheckPlateAvailabilityAsync([FromQuery] string plate, [FromQuery] Guid? excludeVehicleId = null)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<PlateAvailabilityResponseModel>.Success(await userProfileService.CheckPlateAvailabilityAsync(userId, plate, excludeVehicleId)));
    }

    [HttpPost("profile/vehicles")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> AddVehicleAsync([FromBody] CreateVehicleModel model)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<VehicleResponseModel>.Success(await userProfileService.AddVehicleAsync(userId, model)));
    }

    [HttpPut("profile/vehicles/{vehicleId:guid}")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> UpdateVehicleAsync(Guid vehicleId, [FromBody] UpdateVehicleModel model)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<VehicleResponseModel>.Success(await userProfileService.UpdateVehicleAsync(userId, vehicleId, model)));
    }

    [HttpDelete("profile/vehicles/{vehicleId:guid}")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> DeleteVehicleAsync(Guid vehicleId)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<bool>.Success(await userProfileService.DeleteVehicleAsync(userId, vehicleId)));
    }

    [HttpPatch("profile/vehicles/{vehicleId:guid}/default")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> SetDefaultVehicleAsync(Guid vehicleId)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<VehicleResponseModel>.Success(await userProfileService.SetDefaultVehicleAsync(userId, vehicleId)));
    }

    [HttpGet("profile/vehicle-types")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> GetVehicleTypesAsync()
    {
        return Ok(ApiResult<List<VehicleTypeResponseModel>>.Success(await userProfileService.GetVehicleTypesAsync()));
    }

    [HttpPut("profile/change-password")]
    [Tags(SwaggerTags.Profile)]
    [Authorize]
    public async Task<IActionResult> ChangePasswordAsync([FromBody] ChangePasswordModel model)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<bool>.Success(await userProfileService.ChangePasswordAsync(userId, model)));
    }

    [HttpGet("{id}/profile")]
    [Tags(SwaggerTags.Management)]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetProfileByUserIdAsync(string id)
    {
        return Ok(ApiResult<UserProfileResponseModel>.Success(await userProfileService.GetProfileAsync(id)));
    }
}
