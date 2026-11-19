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
    IClaimService claimService,
    IAccountDeletionRequestService deletionRequests) : ApiController
{
    /// <summary>Creates a Manager account and, when lots are given, assigns those lots to it.</summary>
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

    /// <summary>Lists Manager accounts, filtered by active state and a search term (name, email or phone).</summary>
    [HttpGet("managers")]
    public async Task<IActionResult> GetManagersAsync([FromQuery] bool? isActive = null, [FromQuery] string searchTerm = null)
    {
        return Ok(ApiResult<IEnumerable<EmployeeAccountResponseModel>>.Success(
            await employeeAccountService.GetManagersAsync(isActive, searchTerm)));
    }

    /// <summary>One Manager account with the lots it manages.</summary>
    [HttpGet("managers/{id}")]
    public async Task<IActionResult> GetManagerAsync(string id)
    {
        return Ok(ApiResult<EmployeeAccountResponseModel>.Success(await employeeAccountService.GetManagerAsync(id)));
    }

    /// <summary>Updates a Manager's personal and employment details.</summary>
    [HttpPut("managers/{id}")]
    public async Task<IActionResult> UpdateManagerAsync(string id, [FromBody] UpdateEmployeeAccountModel model)
    {
        return Ok(ApiResult<EmployeeAccountResponseModel>.Success(await employeeAccountService.UpdateManagerAsync(id, model)));
    }

    /// <summary>Sets every lot the Manager manages (one Manager per lot; other lots are released).</summary>
    [HttpPut("managers/{id}/lots")]
    public async Task<IActionResult> AssignManagerLotsAsync(string id, [FromBody] AssignParkingLotsModel model)
    {
        return Ok(ApiResult<EmployeeAccountResponseModel>.Success(
            await employeeAccountService.AssignManagerLotsAsync(id, model)));
    }

    /// <summary>Lists internal accounts (Admin, Manager, Staff), filtered by role, active state and a search term.</summary>
    [HttpGet("users")]
    public async Task<IActionResult> GetInternalUsersAsync(
        [FromQuery] string role = null,
        [FromQuery] bool? isActive = null,
        [FromQuery] string searchTerm = null)
    {
        return Ok(ApiResult<IEnumerable<UserResponseModel>>.Success(
            await userService.GetInternalUsersAsync(role, isActive, searchTerm)));
    }

    /// <summary>
    /// Activates or locks an account (internal or customer); Admins cannot lock their own account. Locking a customer
    /// completes their pending account deletion request.
    /// </summary>
    [HttpPatch("users/{id}/status")]
    public async Task<IActionResult> UpdateStatusAsync(string id, [FromBody] UpdateUserStatusModel model)
    {
        var currentUserId = claimService.GetUserId();
        var updated = await userService.UpdateUserStatusAsync(currentUserId, id, model.IsActive);
        if (updated && !model.IsActive)
            await deletionRequests.CompletePendingAsync(id, currentUserId);
        return Ok(ApiResult<bool>.Success(updated));
    }
}
