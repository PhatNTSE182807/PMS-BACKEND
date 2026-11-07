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
public class StaffGateController(IGateService gateService, IReservationService reservationService, PlateListService plateLists, CashDrawerService cashDrawers) : ApiController
{
    /// <summary>The signed-in Staff's cash drawer of today at the lot: float, cash taken, expected banknotes (BR-12).</summary>
    [HttpGet("lots/{lotId:int}/cash-drawer")]
    public async Task<IActionResult> GetCashDrawerAsync(int lotId) =>
        Ok(ApiResult<CashDrawerModel>.Success(await cashDrawers.GetCurrentAsync(lotId)));

    /// <summary>Opens today's drawer with the counted float.</summary>
    [HttpPost("lots/{lotId:int}/cash-drawer/open")]
    public async Task<IActionResult> OpenCashDrawerAsync(int lotId, [FromBody] OpenCashDrawerModel model) =>
        Ok(ApiResult<CashDrawerModel>.Success(await cashDrawers.OpenAsync(lotId, model)));

    /// <summary>Closes the drawer with the blind banknote count; a large difference needs a reason.</summary>
    [HttpPost("lots/{lotId:int}/cash-drawer/close")]
    public async Task<IActionResult> CloseCashDrawerAsync(int lotId, [FromBody] CloseCashDrawerModel model) =>
        Ok(ApiResult<CashDrawerModel>.Success(await cashDrawers.CloseAsync(lotId, model)));

    /// <summary>Asks the lot's Manager for banknotes to add to the open drawer (one waiting request at a time).</summary>
    [HttpPost("lots/{lotId:int}/cash-drawer/top-ups")]
    public async Task<IActionResult> RequestCashTopUpAsync(int lotId, [FromBody] RequestCashTopUpModel model) =>
        Ok(ApiResult<CashTopUpModel>.Success(await cashDrawers.RequestTopUpAsync(lotId, model)));

    /// <summary>Withdraws one's own request before the Manager decides.</summary>
    [HttpPost("lots/{lotId:int}/cash-drawer/top-ups/{id:long}/cancel")]
    public async Task<IActionResult> CancelCashTopUpAsync(int lotId, long id) =>
        Ok(ApiResult<CashTopUpModel>.Success(await cashDrawers.CancelTopUpAsync(lotId, id)));

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
    public async Task<IActionResult> GetHistoryAsync(int lotId, [FromQuery] string plate = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        [FromQuery] string orderBy = null) =>
        Ok(ApiResult<StaffSessionHistoryPageModel>.Success(await gateService.GetHistoryAsync(lotId, plate, page, pageSize,
            string.Equals(orderBy, "exit", StringComparison.OrdinalIgnoreCase))));

    /// <summary>Parking-fee payments taken at an authorised lot, newest first; method=Cash, Wallet, VNPay or MoMo keeps that method only.</summary>
    [HttpGet("lots/{lotId:int}/payments")]
    public async Task<IActionResult> GetPaymentHistoryAsync(int lotId, [FromQuery] string method = null, [FromQuery] string plate = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 10) =>
        Ok(ApiResult<StaffPaymentHistoryPageModel>.Success(await gateService.GetPaymentHistoryAsync(lotId, method, plate, page, pageSize)));

    /// <summary>Exit lane: find the open session by plate, ticket QR or session code.</summary>
    [HttpGet("lots/{lotId:int}/sessions/lookup")]
    public async Task<IActionResult> LookupAsync(int lotId, [FromQuery] string plate = null, [FromQuery] string ticket = null, [FromQuery] string code = null) =>
        Ok(ApiResult<SessionResponseModel>.Success(await gateService.LookupAsync(lotId, plate, ticket, code)));

    /// <summary>Today's and upcoming reservations of the lot.</summary>
    [HttpGet("lots/{lotId:int}/reservations")]
    public async Task<IActionResult> GetReservationsAsync(int lotId, [FromQuery] string status = null, [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null) =>
        Ok(ApiResult<IReadOnlyList<ReservationResponseModel>>.Success(await reservationService.GetForLotAsync(lotId, status, from?.ToUniversalTime(), to?.ToUniversalTime())));

    /// <summary>
    /// Entry lane: the reservation behind a scanned QR, a typed code or, when the customer has no code, the vehicle's
    /// plate (QR, then code, then plate); returns the reservation code and the booked vehicle to fill the form.
    /// </summary>
    [HttpGet("lots/{lotId:int}/reservations/lookup")]
    public async Task<IActionResult> LookupReservationAsync(int lotId, [FromQuery] string code = null, [FromQuery] string qr = null, [FromQuery] string plate = null) =>
        Ok(ApiResult<GateReservationLookupModel>.Success(await reservationService.LookupForGateAsync(lotId, code, qr, plate)));

    /// <summary>Whitelist / Blacklist in force for the lot (read only for Staff).</summary>
    [HttpGet("lots/{lotId:int}/plate-lists")]
    public async Task<IActionResult> GetPlateListsAsync(int lotId, [FromQuery] string listType = null) =>
        Ok(ApiResult<IReadOnlyList<PlateListEntryModel>>.Success(await plateLists.GetAsync(lotId, listType)));

    /// <summary>FR-OPS-01: all gate checks; "Created" opens the barrier, "Blocked" lists what the Staff must resolve.</summary>
    [HttpPost("lots/{lotId:int}/check-in")]
    public async Task<IActionResult> CheckInAsync(int lotId, [FromBody] CheckInModel model) =>
        Ok(ApiResult<CheckInResultModel>.Success(await gateService.CheckInAsync(lotId, model)));

    /// <summary>The vehicle parked in a bay, for the 3D lot model's popup: customer type, plate, time parked, fee so far.</summary>
    [HttpGet("lots/{lotId:int}/slots/{slotId:int}/session")]
    public async Task<IActionResult> GetBaySessionAsync(int lotId, int slotId) =>
        Ok(ApiResult<BaySessionModel>.Success(await gateService.GetBaySessionAsync(lotId, slotId)));

    /// <summary>The same details by session id: a walk-in counted in its zone has no bay to look it up by.</summary>
    [HttpGet("lots/{lotId:int}/sessions/{sessionId:long}/parked")]
    public async Task<IActionResult> GetParkedSessionAsync(int lotId, long sessionId) =>
        Ok(ApiResult<BaySessionModel>.Success(await gateService.GetParkedSessionAsync(lotId, sessionId)));

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

    /// <summary>Lost ticket with the driver's Citizen ID and registration photos; the Staff then approves or refuses it.</summary>
    [HttpPost("sessions/{id:long}/lost-ticket/papers")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 20 * 1024 * 1024)]
    public async Task<IActionResult> ReportLostTicketWithPapersAsync(long id, [FromForm] string reason, IFormFile citizenId, IFormFile registration)
    {
        if (citizenId == null || registration == null)
            return BadRequest(ApiResult<SessionResponseModel>.Failure(["Upload both the Citizen ID and the vehicle registration certificate."]));
        await using var citizen = citizenId.OpenReadStream();
        await using var papers = registration.OpenReadStream();
        return Ok(ApiResult<SessionResponseModel>.Success(await gateService.ReportLostTicketWithPapersAsync(id, new LostTicketPapersModel
        {
            Reason = reason,
            CitizenIdPhoto = citizen,
            CitizenIdLength = citizenId.Length,
            RegistrationPhoto = papers,
            RegistrationLength = registration.Length
        })));
    }

    /// <summary>The Staff compared the papers with the vehicle: approve (fine on the bill, vehicle stays) or refuse.</summary>
    [HttpPost("sessions/{id:long}/lost-ticket/review")]
    public async Task<IActionResult> ReviewLostTicketAsync(long id, [FromBody] LostTicketReviewModel model) =>
        Ok(ApiResult<SessionResponseModel>.Success(await gateService.ReviewLostTicketAsync(id, model)));

    /// <summary>A paper photo of the vehicle's lost-ticket case: citizen-id or registration.</summary>
    [HttpGet("sessions/{id:long}/lost-ticket/papers/{kind}")]
    public async Task<IActionResult> GetLostTicketPaperAsync(long id, string kind)
    {
        var (content, contentType) = await gateService.GetLostTicketPaperAsync(id, kind);
        Response.Headers.CacheControl = "no-store";
        return File(content, contentType);
    }

    /// <summary>FR-OPS-05: lost ticket / card.</summary>
    [HttpPost("sessions/{id:long}/lost-ticket")]
    public async Task<IActionResult> LostTicketAsync(long id, [FromBody] LostTicketModel model) =>
        Ok(ApiResult<SessionResponseModel>.Success(await gateService.ReportLostTicketAsync(id, model)));
}
