using PMS.Application.Models.Operations;

namespace PMS.Application.Services;

/// <summary>
/// Entry and exit lanes of a lot for its Staff (SRS FR-OPS-01..16, UC-01, UC-02), plus the customer's own
/// sessions for paying in the app. Every Staff call is checked against the caller's lot scope.
/// </summary>
public interface IGateService
{
    Task<IReadOnlyList<GateResponseModel>> GetGatesAsync(int lotId);

    Task<IReadOnlyList<ZoneCounterModel>> GetZoneCountersAsync(int lotId);

    /// <summary>Sessions of the lot; by default the open ones (vehicles inside or entering).</summary>
    Task<IReadOnlyList<SessionResponseModel>> GetSessionsAsync(int lotId, string status, string plate);

    Task<SessionResponseModel> GetSessionAsync(long sessionId);

    /// <summary>Find the open session of a plate, a ticket QR or a session code at an exit lane.</summary>
    Task<SessionResponseModel> LookupAsync(int lotId, string plate, string ticket, string code);

    /// <summary>FR-OPS-01: runs every gate check; opens the barrier (session PendingEntry) or returns the blocking alerts.</summary>
    Task<CheckInResultModel> CheckInAsync(int lotId, CheckInModel model);

    /// <summary>The loop sensor saw the vehicle pass: the session becomes valid, slots and counters move.</summary>
    Task<SessionResponseModel> ConfirmEntryAsync(long sessionId);

    /// <summary>The vehicle turned around before the sensor: cancelled, counters unchanged.</summary>
    Task<SessionResponseModel> CancelEntryAsync(long sessionId);

    /// <summary>Fee request at a payment point (FR-OPS-02): the session becomes "Chờ thanh toán" (or Paid when 0 VND).</summary>
    Task<CheckoutQuoteModel> RequestPaymentAsync(long sessionId, CheckoutQuoteRequestModel model);

    /// <summary>The customer drove back in / cancelled paying: back to parked, the next quote starts over.</summary>
    Task<SessionResponseModel> CancelPaymentRequestAsync(long sessionId);

    Task<PaymentResponseModel> PayCashAsync(long sessionId, CashPaymentModel model);

    /// <summary>Dynamic QR at the lane or kiosk (FR-PAY-03).</summary>
    Task<PaymentResponseModel> PayOnlineAtLaneAsync(long sessionId, LanePaymentModel model, PaymentRequestContext context);

    /// <summary>The vehicle passes the exit barrier: session completed, bay and counter released, deposit residual refunded.</summary>
    Task<CheckoutResultModel> CheckoutAsync(long sessionId, CheckoutModel model);

    /// <summary>FR-OPS-05 hook: lost ticket/card with the configured fine; conditional approval (BR-13) or Manager approval.</summary>
    Task<SessionResponseModel> ReportLostTicketAsync(long sessionId, LostTicketModel model);

    /// <summary>Manager decision on a lost ticket case.</summary>
    Task<SessionResponseModel> DecideLostTicketAsync(long sessionId, bool approve, string reason);

    /// <summary>FR-OPS-12 (Manager): close a ghost session; its fee is waived.</summary>
    Task<SessionResponseModel> CloseAbnormallyAsync(long sessionId, string reason);

    /// <summary>The signed-in customer's sessions (FR-CUS-02), open ones first.</summary>
    Task<IReadOnlyList<SessionResponseModel>> GetMySessionsAsync(string userId);

    /// <summary>Fee of the customer's own session, as at a kiosk (prepay through the app).</summary>
    Task<CheckoutQuoteModel> QuoteMySessionAsync(string userId, long sessionId);

    /// <summary>Prepay through the app (FR-PAY-07): an Exit Window starts once paid.</summary>
    Task<PaymentResponseModel> PayMySessionAsync(string userId, long sessionId, CreatePaymentModel model, PaymentRequestContext context);
}
