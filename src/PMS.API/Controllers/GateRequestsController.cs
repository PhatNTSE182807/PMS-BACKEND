using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Operations;
using PMS.Application.Services;
using PMS.Shared.Services;

namespace PMS.API.Controllers;

/// <summary>
/// Check-in / check-out from the customer app. The customer presses "Check in" on a booking or "Check out"
/// on a parked vehicle; the request waits in the lot's queue until a Staff member at the lane opens the
/// barrier (all gate rules still apply) or refuses it. Changes are pushed as "GateRequestChanged" on the
/// parking-ops hub.
/// </summary>
[Authorize]
public class GateRequestsController(IGateRequestService gateRequests, IClaimService claimService) : ApiController
{
    /* ------------------------------------------------------------------ customer */

    /// <summary>Arrived on a confirmed booking, inside its check-in window.</summary>
    [HttpPost("~/api/reservations/{reservationId:long}/check-in-request")]
    [Authorize(Roles = "User")]
    public async Task<IActionResult> RequestCheckInAsync(long reservationId) =>
        Ok(ApiResult<GateRequestModel>.Success(await gateRequests.RequestCheckInAsync(claimService.GetUserId(), reservationId)));

    /// <summary>Wants to drive out a vehicle parked on the account.</summary>
    [HttpPost("~/api/parking-sessions/{sessionId:long}/check-out-request")]
    [Authorize(Roles = "User")]
    public async Task<IActionResult> RequestCheckOutAsync(long sessionId) =>
        Ok(ApiResult<GateRequestModel>.Success(await gateRequests.RequestCheckOutAsync(claimService.GetUserId(), sessionId)));

    /// <summary>The customer's requests of the last 12 hours, newest first.</summary>
    [HttpGet("~/api/gate-requests/mine")]
    [Authorize(Roles = "User")]
    public async Task<IActionResult> GetMineAsync() =>
        Ok(ApiResult<IReadOnlyList<GateRequestModel>>.Success(await gateRequests.GetMineAsync(claimService.GetUserId())));

    [HttpPost("~/api/gate-requests/{requestId:long}/cancel")]
    [Authorize(Roles = "User")]
    public async Task<IActionResult> CancelAsync(long requestId) =>
        Ok(ApiResult<GateRequestModel>.Success(await gateRequests.CancelAsync(claimService.GetUserId(), requestId)));

    /* ------------------------------------------------------------------ Staff */

    /// <summary>Waiting requests of the lot, oldest first; kind = CheckIn or CheckOut (both when empty).</summary>
    [HttpGet("~/api/staff/lots/{lotId:int}/gate-requests")]
    [Authorize(Roles = "Staff,Manager,Admin")]
    public async Task<IActionResult> GetQueueAsync(int lotId, [FromQuery] string kind = null) =>
        Ok(ApiResult<IReadOnlyList<GateRequestModel>>.Success(await gateRequests.GetQueueAsync(lotId, kind)));

    /// <summary>Opens the barrier through the normal gate checks; blocking alerts keep the request waiting.</summary>
    [HttpPost("~/api/staff/gate-requests/{requestId:long}/approve")]
    [Authorize(Roles = "Staff,Manager,Admin")]
    public async Task<IActionResult> ApproveAsync(long requestId, [FromBody] ApproveGateRequestModel model) =>
        Ok(ApiResult<GateRequestDecisionModel>.Success(await gateRequests.ApproveAsync(requestId, model)));

    /// <summary>Refuses the request; the reason is shown to the customer.</summary>
    [HttpPost("~/api/staff/gate-requests/{requestId:long}/refuse")]
    [Authorize(Roles = "Staff,Manager,Admin")]
    public async Task<IActionResult> RefuseAsync(long requestId, [FromBody] RefuseGateRequestModel model) =>
        Ok(ApiResult<GateRequestModel>.Success(await gateRequests.RefuseAsync(requestId, model)));
}
