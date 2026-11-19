using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.User;
using PMS.Application.Services;
using PMS.Shared.Services;

namespace PMS.API.Controllers;

/// <summary>
/// Account deletion requests: a customer asks for their account to be deleted, every Admin is told in their bell, and
/// an Admin locks the account from the customer list (PATCH /api/admin/users/{id}/status), which completes the request.
/// </summary>
public class AccountDeletionRequestsController(
    IAccountDeletionRequestService deletionRequests,
    IClaimService claimService) : ApiController
{
    /// <summary>The signed-in customer's pending deletion request, or null.</summary>
    [HttpGet("~/api/users/profile/deletion-request")]
    [Tags(SwaggerTags.Profile)]
    [Authorize(Roles = "User")]
    public async Task<IActionResult> GetMineAsync() =>
        Ok(ApiResult<AccountDeletionRequestModel>.Success(await deletionRequests.GetMineAsync(claimService.GetUserId())));

    /// <summary>Asks the Admins to delete the signed-in customer's account (one pending request at a time).</summary>
    [HttpPost("~/api/users/profile/deletion-request")]
    [Tags(SwaggerTags.Profile)]
    [Authorize(Roles = "User")]
    public async Task<IActionResult> RequestAsync([FromBody] CreateAccountDeletionRequestModel model) =>
        Ok(ApiResult<AccountDeletionRequestModel>.Success(await deletionRequests.RequestAsync(claimService.GetUserId(), model)));

    /// <summary>Withdraws the signed-in customer's pending deletion request.</summary>
    [HttpDelete("~/api/users/profile/deletion-request")]
    [Tags(SwaggerTags.Profile)]
    [Authorize(Roles = "User")]
    public async Task<IActionResult> CancelMineAsync()
    {
        await deletionRequests.CancelMineAsync(claimService.GetUserId());
        return Ok(ApiResult<object>.Success(null));
    }

    /// <summary>Customer accounts for the Admin (pending deletion requests first), matched on name, user name, email or phone.</summary>
    [HttpGet("~/api/admin/customers")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetCustomersAsync([FromQuery] string searchTerm = null) =>
        Ok(ApiResult<IReadOnlyList<CustomerAccountModel>>.Success(await deletionRequests.GetCustomersAsync(searchTerm)));
}
