using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Account;
using PMS.Application.Models.User;
using PMS.Application.Services;
using PMS.Shared.Services;

namespace PMS.API.Controllers;

/// <summary>A Manager's own lots and Staff. Every call is scoped to the lots the signed-in Manager manages.</summary>
[Route("api/manager")]
[Authorize(Roles = "Manager")]
public class ManagerController(
    IEmployeeAccountService employeeAccountService,
    IClaimService claimService) : ApiController
{
    /// <summary>The lots managed by the signed-in Manager.</summary>
    [HttpGet("lots")]
    public async Task<IActionResult> GetLotsAsync()
    {
        return Ok(ApiResult<IEnumerable<AssignedParkingLotModel>>.Success(
            await employeeAccountService.GetManagedLotsAsync(claimService.GetUserId())));
    }

    /// <summary>Staff of the Manager's lots, filtered by lot, active state and a search term.</summary>
    [HttpGet("staff")]
    public async Task<IActionResult> GetStaffAsync(
        [FromQuery] int? parkingLotId = null,
        [FromQuery] bool? isActive = null,
        [FromQuery] string searchTerm = null)
    {
        return Ok(ApiResult<IEnumerable<EmployeeAccountResponseModel>>.Success(
            await employeeAccountService.GetStaffAsync(claimService.GetUserId(), parkingLotId, isActive, searchTerm)));
    }

    /// <summary>One Staff account of the Manager's lots.</summary>
    [HttpGet("staff/{id}")]
    public async Task<IActionResult> GetStaffByIdAsync(string id)
    {
        return Ok(ApiResult<EmployeeAccountResponseModel>.Success(
            await employeeAccountService.GetStaffByIdAsync(claimService.GetUserId(), id)));
    }

    /// <summary>Creates a Staff account in one of the lots the Manager manages.</summary>
    [HttpPost("staff")]
    public async Task<IActionResult> CreateStaffAsync([FromBody] CreateStaffAccountModel model)
    {
        return Ok(ApiResult<EmployeeAccountResponseModel>.Success(
            await employeeAccountService.CreateStaffAsync(claimService.GetUserId(), model)));
    }

    /// <summary>Updates a Staff member's personal and employment details.</summary>
    [HttpPut("staff/{id}")]
    public async Task<IActionResult> UpdateStaffAsync(string id, [FromBody] UpdateEmployeeAccountModel model)
    {
        return Ok(ApiResult<EmployeeAccountResponseModel>.Success(
            await employeeAccountService.UpdateStaffAsync(claimService.GetUserId(), id, model)));
    }

    /// <summary>Moves a Staff member to another lot managed by the same Manager.</summary>
    [HttpPut("staff/{id}/lot")]
    public async Task<IActionResult> AssignStaffLotAsync(string id, [FromBody] AssignStaffParkingLotModel model)
    {
        return Ok(ApiResult<EmployeeAccountResponseModel>.Success(
            await employeeAccountService.AssignStaffLotAsync(claimService.GetUserId(), id, model)));
    }

    /// <summary>Activates or locks a Staff account.</summary>
    [HttpPatch("staff/{id}/status")]
    public async Task<IActionResult> UpdateStaffStatusAsync(string id, [FromBody] UpdateUserStatusModel model)
    {
        return Ok(ApiResult<EmployeeAccountResponseModel>.Success(
            await employeeAccountService.SetStaffStatusAsync(claimService.GetUserId(), id, model.IsActive)));
    }

    /// <summary>Soft delete: locks the account and marks the employee Terminated.</summary>
    [HttpDelete("staff/{id}")]
    public async Task<IActionResult> DeleteStaffAsync(string id)
    {
        return Ok(ApiResult<bool>.Success(
            await employeeAccountService.DeleteStaffAsync(claimService.GetUserId(), id)));
    }
}
