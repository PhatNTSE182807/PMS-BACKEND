using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Account;
using PMS.Application.Services;
using PMS.Shared.Services;

namespace PMS.API.Controllers;

/// <summary>The signed-in Staff's own work scope.</summary>
[Route("api/staff")]
[Authorize(Roles = "Staff")]
public class StaffController(
    IEmployeeAccountService employeeAccountService,
    IClaimService claimService) : ApiController
{
    /// <summary>The lot the Staff works at, as assigned by their Manager; empty when unassigned.</summary>
    [HttpGet("lots")]
    public async Task<IActionResult> GetLotsAsync()
    {
        return Ok(ApiResult<IEnumerable<AssignedParkingLotModel>>.Success(
            await employeeAccountService.GetStaffLotsAsync(claimService.GetUserId())));
    }
}
