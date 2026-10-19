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
    [HttpGet("lots")]
    public async Task<IActionResult> GetLotsAsync()
    {
        return Ok(ApiResult<IEnumerable<AssignedParkingLotModel>>.Success(
            await employeeAccountService.GetManagedLotsAsync(claimService.GetUserId())));
    }

    [HttpGet("staff")]
    public async Task<IActionResult> GetStaffAsync(
        [FromQuery] int? parkingLotId = null,
        [FromQuery] bool? isActive = null,
        [FromQuery] string searchTerm = null)
    {
        return Ok(ApiResult<IEnumerable<EmployeeAccountResponseModel>>.Success(
            await employeeAccountService.GetStaffAsync(claimService.GetUserId(), parkingLotId, isActive, searchTerm)));
    }

    [HttpGet("staff/{id}")]
    public async Task<IActionResult> GetStaffByIdAsync(string id)
    {
        return Ok(ApiResult<EmployeeAccountResponseModel>.Success(
            await employeeAccountService.GetStaffByIdAsync(claimService.GetUserId(), id)));
    }

    [HttpPost("staff")]
    public async Task<IActionResult> CreateStaffAsync([FromBody] CreateStaffAccountModel model)
    {
        return Ok(ApiResult<EmployeeAccountResponseModel>.Success(
            await employeeAccountService.CreateStaffAsync(claimService.GetUserId(), model)));
    }

    [HttpPut("staff/{id}")]
    public async Task<IActionResult> UpdateStaffAsync(string id, [FromBody] UpdateEmployeeAccountModel model)
    {
        return Ok(ApiResult<EmployeeAccountResponseModel>.Success(
            await employeeAccountService.UpdateStaffAsync(claimService.GetUserId(), id, model)));
    }

    [HttpPut("staff/{id}/lot")]
    public async Task<IActionResult> AssignStaffLotAsync(string id, [FromBody] AssignStaffParkingLotModel model)
    {
        return Ok(ApiResult<EmployeeAccountResponseModel>.Success(
            await employeeAccountService.AssignStaffLotAsync(claimService.GetUserId(), id, model)));
    }

    [HttpPatch("staff/{id}/status")]
    public async Task<IActionResult> UpdateStaffStatusAsync(string id, [FromBody] UpdateUserStatusModel model)
    {
        return Ok(ApiResult<EmployeeAccountResponseModel>.Success(
            await employeeAccountService.SetStaffStatusAsync(claimService.GetUserId(), id, model.IsActive)));
    }

    /// <summary>Soft delete (BR-21): locks the account and marks the employee Terminated.</summary>
    [HttpDelete("staff/{id}")]
    public async Task<IActionResult> DeleteStaffAsync(string id)
    {
        return Ok(ApiResult<bool>.Success(
            await employeeAccountService.DeleteStaffAsync(claimService.GetUserId(), id)));
    }
}
