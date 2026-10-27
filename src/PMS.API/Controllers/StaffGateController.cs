using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Operations;
using PMS.Application.Services;
using PMS.Application.Services.Impl;

namespace PMS.API.Controllers;

/// <summary>
/// Gate operations of a lot (SRS FR-OPS, UC-01, UC-02): check-in, sensor confirmation, payment points
/// and check-out. Staff act on their assigned lot; Managers on the lots they manage.
/// </summary>
[Route("api/staff")]
[Authorize(Roles = "Staff,Manager,Admin")]
public class StaffGateController(IGateService gateService, IReservationService reservationService, PlateListService plateLists) : ApiController
{
    [HttpGet("lots/{lotId:int}/gates")]
    public async Task<IActionResult> GetGatesAsync(int lotId) =>
        Ok(ApiResult<IReadOnlyList<GateResponseModel>>.Success(await gateService.GetGatesAsync(lotId)));

    /// <summary>Zone Counters of the lot (FR-LOT-03).</summary>
    [HttpGet("lots/{lotId:int}/zone-counters")]
    public async Task<IActionResult> GetZoneCountersAsync(int lotId) =>
        Ok(ApiResult<IReadOnlyList<ZoneCounterModel>>.Success(await gateService.GetZoneCountersAsync(lotId)));

    /// <summary>Open sessions (default) or sessions with a status; optional plate filter.</summary>
    [HttpGet("lots/{lotId:int}/sessions")]
    public async Task<IActionResult> GetSessionsAsync(int lotId, [FromQuery] string status = null, [FromQuery] string plate = null) =>
        Ok(ApiResult<IReadOnlyList<SessionResponseModel>>.Success(await gateService.GetSessionsAsync(lotId, status, plate)));

    /// <summary>Parking session history for an authorised lot, newest entry first.</summary>
    [HttpGet("lots/{lotId:int}/sessions/history")]
    public async Task<IActionResult> GetHistoryAsync(int lotId, [FromQuery] string plate = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 10) =>
        Ok(ApiResult<StaffSessionHistoryPageModel>.Success(await gateService.GetHistoryAsync(lotId, plate, page, pageSize)));

    /// <summary>Exit lane: find the open session by plate, ticket QR or session code.</summary>
    [HttpGet("lots/{lotId:int}/sessions/lookup")]
    public async Task<IActionResult> LookupAsync(int lotId, [FromQuery] string plate = null, [FromQuery] string ticket = null, [FromQuery] string code = null) =>
        Ok(ApiResult<SessionResponseModel>.Success(await gateService.LookupAsync(lotId, plate, ticket, code)));

    /// <summary>Today's and upcoming reservations of the lot.</summary>
    [HttpGet("lots/{lotId:int}/reservations")]
    public async Task<IActionResult> GetReservationsAsync(int lotId, [FromQuery] string status = null, [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null) =>
        Ok(ApiResult<IReadOnlyList<ReservationResponseModel>>.Success(await reservationService.GetForLotAsync(lotId, status, from?.ToUniversalTime(), to?.ToUniversalTime())));

    /// <summary>Entry lane: the reservation behind a scanned QR or typed code, with the booked vehicle to fill the form.</summary>
    [HttpGet("lots/{lotId:int}/reservations/lookup")]
    public async Task<IActionResult> LookupReservationAsync(int lotId, [FromQuery] string code = null, [FromQuery] string qr = null) =>
        Ok(ApiResult<GateReservationLookupModel>.Success(await reservationService.LookupForGateAsync(lotId, code, qr)));

    /// <summary>Whitelist / Blacklist in force for the lot (read only for Staff).</summary>
    [HttpGet("lots/{lotId:int}/plate-lists")]
    public async Task<IActionResult> GetPlateListsAsync(int lotId, [FromQuery] string listType = null) =>
        Ok(ApiResult<IReadOnlyList<PlateListEntryModel>>.Success(await plateLists.GetAsync(lotId, listType)));

    /// <summary>FR-OPS-01: all gate checks; "Created" opens the barrier, "Blocked" lists what the Staff must resolve.</summary>
    [HttpPost("lots/{lotId:int}/check-in")]
    public async Task<IActionResult> CheckInAsync(int lotId, [FromBody] CheckInModel model) =>
        Ok(ApiResult<CheckInResultModel>.Success(await gateService.CheckInAsync(lotId, model)));

    [HttpGet("sessions/{id:long}")]
    public async Task<IActionResult> GetSessionAsync(long id) =>
        Ok(ApiResult<SessionResponseModel>.Success(await gateService.GetSessionAsync(id)));

    /// <summary>Loop sensor: the vehicle passed the entry barrier, the session is now valid.</summary>
    [HttpPost("sessions/{id:long}/entry-confirm")]
    public async Task<IActionResult> ConfirmEntryAsync(long id) =>
        Ok(ApiResult<SessionResponseModel>.Success(await gateService.ConfirmEntryAsync(id)));

    /// <summary>The vehicle turned around before the sensor.</summary>
    [HttpPost("sessions/{id:long}/entry-cancel")]
    public async Task<IActionResult> CancelEntryAsync(long id) =>
        Ok(ApiResult<SessionResponseModel>.Success(await gateService.CancelEntryAsync(id)));

    /// <summary>Fee request at the exit lane or counter: deposit deducted, Exit Window checked.</summary>
    [HttpPost("sessions/{id:long}/quote")]
    public async Task<IActionResult> QuoteAsync(long id, [FromBody] CheckoutQuoteRequestModel model) =>
        Ok(ApiResult<CheckoutQuoteModel>.Success(await gateService.RequestPaymentAsync(id, model)));

    [HttpPost("sessions/{id:long}/cancel-payment")]
    public async Task<IActionResult> CancelPaymentAsync(long id) =>
        Ok(ApiResult<SessionResponseModel>.Success(await gateService.CancelPaymentRequestAsync(id)));

    [HttpPost("sessions/{id:long}/payments/cash")]
    public async Task<IActionResult> PayCashAsync(long id, [FromBody] CashPaymentModel model) =>
        Ok(ApiResult<PaymentResponseModel>.Success(await gateService.PayCashAsync(id, model)));

    /// <summary>Dynamic QR (VNPay / MoMo) at the lane for a Guest; show payUrl as a QR code.</summary>
    [HttpPost("sessions/{id:long}/payments/online")]
    public async Task<IActionResult> PayOnlineAsync(long id, [FromBody] LanePaymentModel model) =>
        Ok(ApiResult<PaymentResponseModel>.Success(await gateService.PayOnlineAtLaneAsync(id, model, PaymentsController.RequestContext(HttpContext))));

    /// <summary>The vehicle passes the exit barrier (paid or 0 VND).</summary>
    [HttpPost("sessions/{id:long}/check-out")]
    public async Task<IActionResult> CheckoutAsync(long id, [FromBody] CheckoutModel model) =>
        Ok(ApiResult<CheckoutResultModel>.Success(await gateService.CheckoutAsync(id, model)));

    /// <summary>FR-OPS-05: lost ticket / card.</summary>
    [HttpPost("sessions/{id:long}/lost-ticket")]
    public async Task<IActionResult> LostTicketAsync(long id, [FromBody] LostTicketModel model) =>
        Ok(ApiResult<SessionResponseModel>.Success(await gateService.ReportLostTicketAsync(id, model)));
}
