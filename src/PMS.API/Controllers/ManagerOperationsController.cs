using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Operations;
using PMS.Application.Models.ParkingLot;
using PMS.Application.Services;
using PMS.Application.Services.Impl;

namespace PMS.API.Controllers;

/// <summary>Manager decisions on reservations and sessions of the lots they manage.</summary>
[Route("api/manager")]
[Authorize(Roles = "Manager,Admin")]
public class ManagerOperationsController(
    IReservationService reservationService,
    IGateService gateService,
    PlateListService plateLists,
    IParkingLotService parkingLots,
    LotScopeService lotScope,
    CashDrawerService cashDrawers) : ApiController
{
    /// <summary>Staff cash closes of the lot: waiting for confirmation by default, or one status.</summary>
    [HttpGet("lots/{lotId:int}/cash-closes")]
    public async Task<IActionResult> GetCashClosesAsync(int lotId, [FromQuery] string status = null) =>
        Ok(ApiResult<IReadOnlyList<CashDrawerModel>>.Success(await cashDrawers.GetForLotAsync(lotId, status)));

    /// <summary>Confirms a Staff cash close (never one's own); a difference needs a note.</summary>
    [HttpPost("cash-closes/{id:long}/confirm")]
    public async Task<IActionResult> ConfirmCashCloseAsync(long id, [FromBody] ConfirmCashCloseModel model) =>
        Ok(ApiResult<CashDrawerModel>.Success(await cashDrawers.ConfirmAsync(id, model)));

    /// <summary>Live occupied and free bays of a lot the Manager runs: in total, per vehicle family and per zone.</summary>
    [HttpGet("lots/{lotId:int}/occupancy")]
    public async Task<IActionResult> GetOccupancyAsync(int lotId)
    {
        await lotScope.EnsureCanManageAsync(lotId);
        return Ok(ApiResult<LotOccupancyModel>.Success(await parkingLots.GetOccupancyAsync(lotId)));
    }

    [HttpGet("lots/{lotId:int}/reservations")]
    public async Task<IActionResult> GetReservationsAsync(int lotId, [FromQuery] string status = null, [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null) =>
        Ok(ApiResult<IReadOnlyList<ReservationResponseModel>>.Success(await reservationService.GetForLotAsync(lotId, status, from?.ToUniversalTime(), to?.ToUniversalTime())));

    /// <summary>FR-RSV-09: lot-side cancellation with a 100 % refund.</summary>
    [HttpPost("reservations/{id:long}/cancel")]
    public async Task<IActionResult> CancelReservationAsync(long id, [FromBody] CancelReservationModel model) =>
        Ok(ApiResult<ReservationResponseModel>.Success(await reservationService.CancelByLotAsync(id, model?.Reason)));

    /// <summary>FR-OPS-12: close a ghost session (fee waived).</summary>
    [HttpPost("sessions/{id:long}/abnormal-close")]
    public async Task<IActionResult> CloseAbnormallyAsync(long id, [FromBody] SessionDecisionModel model) =>
        Ok(ApiResult<SessionResponseModel>.Success(await gateService.CloseAbnormallyAsync(id, model?.Reason)));

    [HttpPost("sessions/{id:long}/lost-ticket/approve")]
    public async Task<IActionResult> ApproveLostTicketAsync(long id, [FromBody] SessionDecisionModel model) =>
        Ok(ApiResult<SessionResponseModel>.Success(await gateService.DecideLostTicketAsync(id, true, model?.Reason)));

    [HttpPost("sessions/{id:long}/lost-ticket/reject")]
    public async Task<IActionResult> RejectLostTicketAsync(long id, [FromBody] SessionDecisionModel model) =>
        Ok(ApiResult<SessionResponseModel>.Success(await gateService.DecideLostTicketAsync(id, false, model?.Reason)));

    [HttpGet("lots/{lotId:int}/plate-lists")]
    public async Task<IActionResult> GetPlateListsAsync(int lotId, [FromQuery] string listType = null) =>
        Ok(ApiResult<IReadOnlyList<PlateListEntryModel>>.Success(await plateLists.GetAsync(lotId, listType)));

    /// <summary>FR-OPS-07: add a Whitelist / Blacklist plate for the lot (reason and expiry required).</summary>
    [HttpPost("lots/{lotId:int}/plate-lists")]
    public async Task<IActionResult> AddPlateListEntryAsync(int lotId, [FromBody] CreatePlateListEntryModel model) =>
        Ok(ApiResult<PlateListEntryModel>.Success(await plateLists.AddAsync(lotId, model)));

    [HttpDelete("lots/{lotId:int}/plate-lists/{id:int}")]
    public async Task<IActionResult> RemovePlateListEntryAsync(int lotId, int id) =>
        Ok(ApiResult<bool>.Success(await plateLists.DeactivateAsync(lotId, id)));
}
