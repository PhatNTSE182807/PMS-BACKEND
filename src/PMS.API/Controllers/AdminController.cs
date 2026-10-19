using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Account;
using PMS.Application.Models.Admin;
using PMS.Application.Models.User;
using PMS.Application.Services;
using PMS.Shared.Services;

namespace PMS.API.Controllers;

[Route("api/admin")]
[Authorize(Roles = "Admin")]
public class AdminController(
    IUserService userService,
    IEmployeeAccountService employeeAccountService,
    IClaimService claimService) : ApiController
{
    [HttpPost("managers")]
    public async Task<IActionResult> CreateManagerAsync([FromBody] CreateEmployeeAccountModel model)
    {
        var created = await userService.CreateManagerAsync(model);
        if (model.ParkingLotIds is { Count: > 0 })
        {
            await employeeAccountService.AssignManagerLotsAsync(
                created.Id.ToString(), new AssignParkingLotsModel { ParkingLotIds = model.ParkingLotIds });
        }

        return Ok(ApiResult<CreateUserResponseModel>.Success(created));
    }

    [HttpGet("managers")]
    public async Task<IActionResult> GetManagersAsync([FromQuery] bool? isActive = null, [FromQuery] string searchTerm = null)
    {
        return Ok(ApiResult<IEnumerable<EmployeeAccountResponseModel>>.Success(
            await employeeAccountService.GetManagersAsync(isActive, searchTerm)));
    }

    [HttpGet("managers/{id}")]
    public async Task<IActionResult> GetManagerAsync(string id)
    {
        return Ok(ApiResult<EmployeeAccountResponseModel>.Success(await employeeAccountService.GetManagerAsync(id)));
    }

    [HttpPut("managers/{id}")]
    public async Task<IActionResult> UpdateManagerAsync(string id, [FromBody] UpdateEmployeeAccountModel model)
    {
        return Ok(ApiResult<EmployeeAccountResponseModel>.Success(await employeeAccountService.UpdateManagerAsync(id, model)));
    }

    [HttpPatch("managers/{id}/status")]
    public async Task<IActionResult> UpdateManagerStatusAsync(string id, [FromBody] UpdateUserStatusModel model)
    {
        return Ok(ApiResult<EmployeeAccountResponseModel>.Success(
            await employeeAccountService.SetManagerStatusAsync(id, model.IsActive)));
    }

    /// <summary>Sets every lot the Manager manages (one Manager per lot; other lots are released).</summary>
    [HttpPut("managers/{id}/lots")]
    public async Task<IActionResult> AssignManagerLotsAsync(string id, [FromBody] AssignParkingLotsModel model)
    {
        return Ok(ApiResult<EmployeeAccountResponseModel>.Success(
            await employeeAccountService.AssignManagerLotsAsync(id, model)));
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
