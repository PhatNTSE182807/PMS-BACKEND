using PMS.Application.Models.Operations;

namespace PMS.Application.Services;

/// <summary>
/// Entry and exit lanes of a lot for its Staff, plus the customer's own
/// sessions for paying in the app. Every Staff call is checked against the caller's lot scope.
/// </summary>
public interface IGateService
{
    Task<IReadOnlyList<GateResponseModel>> GetGatesAsync(int lotId);

    Task<IReadOnlyList<ZoneCounterModel>> GetZoneCountersAsync(int lotId);

    /// <summary>Sessions of the lot; by default the open ones (vehicles inside or entering).</summary>
    Task<IReadOnlyList<SessionResponseModel>> GetSessionsAsync(int lotId, string status, string plate);

    Task<StaffSessionHistoryPageModel> GetHistoryAsync(int lotId, string plate, int page, int pageSize, bool byExit = false);

    /// <summary>Parking-fee payments taken at the lot, newest first; <paramref name="method"/> "Cash" keeps cash only.</summary>
    Task<StaffPaymentHistoryPageModel> GetPaymentHistoryAsync(int lotId, string method, string plate, int page, int pageSize);

    Task<SessionResponseModel> GetSessionAsync(long sessionId);

    /// <summary>
    /// The open session parked in a bay of the lot, with the customer type, time parked and a read-only fee so far
    /// (the 3D lot model's bay popup). Lot-scoped like every Staff call; NotFound when no vehicle is tracked in the bay.
    /// </summary>
    Task<BaySessionModel> GetBaySessionAsync(int lotId, int slotId);

    /// <summary>
    /// The same details for one open session of the lot, found by its id: a walk-in counted in its zone has no bay,
    /// so the 3D model opens it this way. Lot-scoped; NotFound when the session is not open in this lot.
    /// </summary>
    Task<BaySessionModel> GetParkedSessionAsync(int lotId, long sessionId);

    /// <summary>Find the open session of a plate, a ticket QR or a session code at an exit lane.</summary>
    Task<SessionResponseModel> LookupAsync(int lotId, string plate, string ticket, string code);

    /// <summary>Runs every gate check; opens the barrier (session PendingEntry) or returns the blocking alerts.</summary>
    Task<CheckInResultModel> CheckInAsync(int lotId, CheckInModel model);

    /// <summary>The loop sensor saw the vehicle pass: the session becomes valid, slots and counters move.</summary>
    Task<SessionResponseModel> ConfirmEntryAsync(long sessionId);

    /// <summary>Fee request at a payment point: the session becomes "Chờ thanh toán" (or Paid when 0 VND).</summary>
    Task<CheckoutQuoteModel> RequestPaymentAsync(long sessionId, CheckoutQuoteRequestModel model);

    /// <summary>The customer drove back in / cancelled paying: back to parked, the next quote starts over.</summary>
    Task<SessionResponseModel> CancelPaymentRequestAsync(long sessionId);

    Task<PaymentResponseModel> PayCashAsync(long sessionId, CashPaymentModel model);

    /// <summary>Dynamic QR at the lane or kiosk.</summary>
    Task<PaymentResponseModel> PayOnlineAtLaneAsync(long sessionId, LanePaymentModel model, PaymentRequestContext context);

    /// <summary>The vehicle passes the exit barrier: session completed, bay and counter released, deposit residual refunded.</summary>
    Task<CheckoutResultModel> CheckoutAsync(long sessionId, CheckoutModel model);

    /// <summary>Lost ticket/card with the configured fine; conditional approval or Manager approval.</summary>
    Task<SessionResponseModel> ReportLostTicketAsync(long sessionId, LostTicketModel model);

    /// <summary>Lost ticket with the driver's Citizen ID and registration photos, waiting for the Staff's check.</summary>
    Task<SessionResponseModel> ReportLostTicketWithPapersAsync(long sessionId, LostTicketPapersModel model);

    /// <summary>The Staff approves (fine on the bill, vehicle stays) or refuses the papers.</summary>
    Task<SessionResponseModel> ReviewLostTicketAsync(long sessionId, LostTicketReviewModel model);

    /// <summary>One uploaded paper of the latest lost-ticket case.</summary>
    Task<(Stream Content, string ContentType)> GetLostTicketPaperAsync(long sessionId, string kind);

    /// <summary>Manager only: close a ghost session; its fee is waived.</summary>
    Task<SessionResponseModel> CloseAbnormallyAsync(long sessionId, string reason);

    /// <summary>Prepay through the app: an Exit Window starts once paid.</summary>
    Task<PaymentResponseModel> PayMySessionAsync(string userId, long sessionId, CreatePaymentModel model, PaymentRequestContext context);

    /// <summary>The customer's vehicles inside a lot now, newest entry first, with the running fee and the exit pass.</summary>
    Task<IReadOnlyList<CurrentSessionModel>> GetMyCurrentSessionsAsync(string userId);
}
