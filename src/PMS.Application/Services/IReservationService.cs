using PMS.Application.Models.Operations;

namespace PMS.Application.Services;

/// <summary>Customer reservations (SRS FR-RSV-02..09, UC-04) and the Manager's lot-side cancellation.</summary>
public interface IReservationService
{
    /// <summary>Holds the slot at once (first holder wins) and starts the deposit deadline.</summary>
    Task<ReservationResponseModel> CreateAsync(string userId, CreateReservationModel model);

    Task<IReadOnlyList<ReservationResponseModel>> GetMineAsync(string userId, bool activeOnly);

    Task<ReservationResponseModel> GetAsync(string userId, long id);

    /// <summary>The customer's next booking that has not started (soonest arrival), or null.</summary>
    Task<ReservationResponseModel> GetUpcomingAsync(string userId);

    /// <summary>FR-RSV-03: new arrival time (within BR-01) and/or another free slot, before the change deadline.</summary>
    Task<ReservationResponseModel> UpdateAsync(string userId, long id, UpdateReservationModel model);

    /// <summary>FR-RSV-03 / BR-04: cancel; refunds 100 % up to H minutes before arrival, nothing after.</summary>
    Task<ReservationResponseModel> CancelAsync(string userId, long id, string reason);

    /// <summary>FR-RSV-04: signed QR for the check-in window.</summary>
    Task<ReservationQrModel> GetQrAsync(string userId, long id);

    /// <summary>FR-RSV-09: the lot cancels (closure, maintenance): 100 % refund and a notification.</summary>
    Task<ReservationResponseModel> CancelByLotAsync(long id, string reason);

    /// <summary>Reservations of a lot for its Staff and Managers.</summary>
    Task<IReadOnlyList<ReservationResponseModel>> GetForLotAsync(int lotId, string status, DateTime? fromUtc, DateTime? toUtc);

    /// <summary>Entry lane: the booking behind a scanned QR or typed code, its vehicle and whether it can check in now.</summary>
    Task<GateReservationLookupModel> LookupForGateAsync(int lotId, string code, string qr, string plate = null);
}
