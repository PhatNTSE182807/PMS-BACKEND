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

public class UsersController(
    IUserService userService,
    IUserProfileService userProfileService,
    IClaimService claimService) : ApiController
{
    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAllAsync([FromQuery] string role = null, [FromQuery] bool? isActive = null, [FromQuery] string searchTerm = null)
    {
        return Ok(ApiResult<IEnumerable<UserResponseModel>>.Success(await userService.GetAllAsync(role, isActive, searchTerm)));
    }

    [HttpGet("{id}")]
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
    [AllowAnonymous]
    public async Task<IActionResult> RegisterAsync(CreateUserModel createUserModel)
    {
        return Ok(ApiResult<CreateUserResponseModel>.Success(await userService.CreateAsync(createUserModel)));
    }

    [HttpPost("authenticate")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginAsync(LoginUserModel loginUserModel)
    {
        return Ok(ApiResult<LoginResponseModel>.Success(await userService.LoginAsync(loginUserModel)));
    }

    [HttpPost("two-factor")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyTwoFactorAsync([FromBody] TwoFactorLoginModel model)
    {
        return Ok(ApiResult<LoginResponseModel>.Success(await userService.VerifyTwoFactorAsync(model)));
    }

    [HttpPost("two-factor/resend")]
    [AllowAnonymous]
    public async Task<IActionResult> ResendTwoFactorOtpAsync([FromBody] ResendTwoFactorModel model)
    {
        return Ok(ApiResult<ResendTwoFactorResponseModel>.Success(await userService.ResendTwoFactorOtpAsync(model)));
    }

    [HttpPost("login-otp/verify")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyLoginOtpAsync([FromBody] TwoFactorLoginModel model)
    {
        return Ok(ApiResult<LoginResponseModel>.Success(await userService.VerifyTwoFactorAsync(model)));
    }

    [HttpPost("login-otp/request")]
    [AllowAnonymous]
    public async Task<IActionResult> RequestLoginOtpAsync([FromBody] ResendTwoFactorModel model)
    {
        return Ok(ApiResult<ResendTwoFactorResponseModel>.Success(await userService.ResendTwoFactorOtpAsync(model)));
    }

    [HttpPost("activate")]
    [AllowAnonymous]
    public async Task<IActionResult> ActivateAccountAsync([FromBody] ActivateAccountModel model)
    {
        return Ok(ApiResult<bool>.Success(await userService.ActivateAccountAsync(model)));
    }

    [HttpGet("profile")]
    [Authorize]
    public async Task<IActionResult> GetProfileAsync()
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<UserProfileResponseModel>.Success(await userProfileService.GetProfileAsync(userId)));
    }

    [HttpPut("profile")]
    [Authorize]
    public async Task<IActionResult> UpdateProfileAsync([FromBody] UpdateUserProfileModel model)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<UserProfileResponseModel>.Success(await userProfileService.UpdateProfileAsync(userId, model)));
    }

    [HttpGet("profile/vehicles")]
    [Authorize]
    public async Task<IActionResult> GetVehiclesAsync()
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<List<VehicleResponseModel>>.Success(await userProfileService.GetVehiclesAsync(userId)));
    }

    [HttpPost("profile/vehicles")]
    [Authorize]
    public async Task<IActionResult> AddVehicleAsync([FromBody] CreateVehicleModel model)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<VehicleResponseModel>.Success(await userProfileService.AddVehicleAsync(userId, model)));
    }

    [HttpPut("profile/vehicles/{vehicleId:guid}")]
    [Authorize]
    public async Task<IActionResult> UpdateVehicleAsync(Guid vehicleId, [FromBody] UpdateVehicleModel model)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<VehicleResponseModel>.Success(await userProfileService.UpdateVehicleAsync(userId, vehicleId, model)));
    }

    [HttpDelete("profile/vehicles/{vehicleId:guid}")]
    [Authorize]
    public async Task<IActionResult> DeleteVehicleAsync(Guid vehicleId)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<bool>.Success(await userProfileService.DeleteVehicleAsync(userId, vehicleId)));
    }

    [HttpPatch("profile/vehicles/{vehicleId:guid}/default")]
    [Authorize]
    public async Task<IActionResult> SetDefaultVehicleAsync(Guid vehicleId)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<VehicleResponseModel>.Success(await userProfileService.SetDefaultVehicleAsync(userId, vehicleId)));
    }

    [HttpGet("profile/vehicle-types")]
    [Authorize]
    public async Task<IActionResult> GetVehicleTypesAsync()
    {
        return Ok(ApiResult<List<VehicleTypeResponseModel>>.Success(await userProfileService.GetVehicleTypesAsync()));
    }

    [HttpPut("profile/change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePasswordAsync([FromBody] ChangePasswordModel model)
    {
        var userId = claimService.GetUserId();
        return Ok(ApiResult<bool>.Success(await userProfileService.ChangePasswordAsync(userId, model)));
    }

    [HttpGet("{id}/profile")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetProfileByUserIdAsync(string id)
    {
        return Ok(ApiResult<UserProfileResponseModel>.Success(await userProfileService.GetProfileAsync(id)));
    }
}
