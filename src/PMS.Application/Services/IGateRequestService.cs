using PMS.Application.Models.Operations;

namespace PMS.Application.Services;

/// <summary>
/// "Check in" / "Check out" pressed in the customer app: the request joins the lot's queue, and a Staff
/// member at the lane opens the barrier (through the normal gate checks) or refuses it. Requests and their
/// outcome are pushed live to the lot's Staff and to the customer.
/// </summary>
public interface IGateRequestService
{
    /// <summary>The customer arrived on a confirmed booking (inside its check-in window).</summary>
    Task<GateRequestModel> RequestCheckInAsync(string userId, long reservationId);

    /// <summary>The customer wants to drive out a vehicle parked on their account.</summary>
    Task<GateRequestModel> RequestCheckOutAsync(string userId, long sessionId);

    /// <summary>The customer withdraws a waiting request.</summary>
    Task<GateRequestModel> CancelAsync(string userId, long requestId);

    /// <summary>The customer's requests of the last 12 hours, newest first.</summary>
    Task<IReadOnlyList<GateRequestModel>> GetMineAsync(string userId);

    /// <summary>Waiting requests of a lot (one kind or both), oldest first. Requests that no longer apply are closed.</summary>
    Task<IReadOnlyList<GateRequestModel>> GetQueueAsync(int lotId, string kind);

    /// <summary>
    /// Force check-out of a walk-in from the 3D lot model: the Staff takes what it owes in cash now and the vehicle
    /// waits at the exit lane, where the Staff opens the barrier (Check-out tab) and the exit cameras show it leave.
    /// </summary>
    Task<GateRequestModel> ForceCheckOutAsync(long sessionId);

    /// <summary>
    /// A simulated walk-in drives up to the lot's entry lane and waits in the check-in queue (Admin parking
    /// simulation): the Staff checks it in at the lane into <paramref name="slotId"/>, like a real walk-in.
    /// </summary>
    Task<GateRequestModel> QueueSimulatedWalkInAsync(int lotId, string plate, int vehicleTypeId, int slotId, TimeSpan stay, string adminId);

    /// <summary>Opens the barrier through the normal gate rules; blocked checks leave the request waiting.</summary>
    Task<GateRequestDecisionModel> ApproveAsync(long requestId, ApproveGateRequestModel model);

    /// <summary>Staff refuse the request with a reason for the customer.</summary>
    Task<GateRequestModel> RefuseAsync(long requestId, RefuseGateRequestModel model);
}
