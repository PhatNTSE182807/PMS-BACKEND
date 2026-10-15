using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Admin;
using PMS.Application.Models.User;
using PMS.Application.Services;
using PMS.Shared.Services;

namespace PMS.API.Controllers;

[Route("api/admin")]
[Authorize(Roles = "Admin")]
public class AdminController(
    IUserService userService,
    IClaimService claimService) : ApiController
{
    [HttpPost("managers")]
    public async Task<IActionResult> CreateManagerAsync([FromBody] CreateEmployeeAccountModel model)
    {
        return Ok(ApiResult<CreateUserResponseModel>.Success(await userService.CreateManagerAsync(model)));
    }

    [HttpPost("staff")]
    public async Task<IActionResult> CreateStaffAsync([FromBody] CreateEmployeeAccountModel model)
    {
        return Ok(ApiResult<CreateUserResponseModel>.Success(await userService.CreateStaffAsync(model)));
    }

    [HttpGet("users")]
    public async Task<IActionResult> GetInternalUsersAsync(
        [FromQuery] string role = null,
        [FromQuery] bool? isActive = null,
        [FromQuery] string searchTerm = null)
    {
        return Ok(ApiResult<IEnumerable<UserResponseModel>>.Success(
            await userService.GetInternalUsersAsync(role, isActive, searchTerm)));
    }

    [HttpPatch("users/{id}/status")]
    public async Task<IActionResult> UpdateStatusAsync(string id, [FromBody] UpdateUserStatusModel model)
    {
        var currentUserId = claimService.GetUserId();
        return Ok(ApiResult<bool>.Success(await userService.UpdateUserStatusAsync(currentUserId, id, model.IsActive)));
    }
}
