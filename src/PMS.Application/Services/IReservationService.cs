using PMS.Application.Models.Operations;

namespace PMS.Application.Services;

/// <summary>Customer reservations and the Manager's lot-side cancellation.</summary>
public interface IReservationService
{
    /// <summary>Holds the slot at once (first holder wins) and starts the deposit deadline.</summary>
    Task<ReservationResponseModel> CreateAsync(string userId, CreateReservationModel model);

    /// <summary>Expected state of every bay of the lot during the window to book (arrival, and departure when Scheduled).</summary>
    Task<IReadOnlyList<BayForecastModel>> GetBayForecastAsync(int lotId, DateTimeOffset arrival, DateTimeOffset? departure);

    Task<IReadOnlyList<ReservationResponseModel>> GetMineAsync(string userId, bool activeOnly);

    Task<ReservationResponseModel> GetAsync(string userId, long id);

    /// <summary>The customer's next booking that has not started (soonest arrival), or null.</summary>
    Task<ReservationResponseModel> GetUpcomingAsync(string userId);

    /// <summary>New arrival time (within the booking window) and/or another free slot, before the change deadline.</summary>
    Task<ReservationResponseModel> UpdateAsync(string userId, long id, UpdateReservationModel model);

    /// <summary>Cancel; refunds 100 % up to H minutes before arrival, nothing after.</summary>
    Task<ReservationResponseModel> CancelAsync(string userId, long id, string reason);

    /// <summary>Signed QR for the check-in window.</summary>
    Task<ReservationQrModel> GetQrAsync(string userId, long id);

    /// <summary>The lot cancels (closure, maintenance): 100 % refund and a notification.</summary>
    Task<ReservationResponseModel> CancelByLotAsync(long id, string reason);

    /// <summary>Reservations of a lot for its Staff and Managers.</summary>
    Task<IReadOnlyList<ReservationResponseModel>> GetForLotAsync(int lotId, string status, DateTime? fromUtc, DateTime? toUtc);

    /// <summary>Entry lane: the booking behind a scanned QR or typed code, its vehicle and whether it can check in now.</summary>
    Task<GateReservationLookupModel> LookupForGateAsync(int lotId, string code, string qr, string plate = null);
}
