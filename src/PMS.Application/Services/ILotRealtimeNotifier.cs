using System.Threading.Tasks;

namespace PMS.Application.Services;

/// <summary>
/// Pushes lot changes to the clients watching that lot (SignalR in PMS.API, SRS FR-3D-03/08).
/// Public payloads carry ids and statuses only, never plates.
/// </summary>
public interface ILotRealtimeNotifier
{
    Task SlotStatusChangedAsync(int lotId, int slotId, string status);

    Task LotStatusChangedAsync(int lotId, string status);

    /// <summary>The lot's 3D layout was (re)generated, so clients reload it.</summary>
    Task LayoutChangedAsync(int lotId);

    /// <summary>A price of the lot is now in force ("Applied") or will start once the lot is empty ("Scheduled").</summary>
    Task LotPriceChangedAsync(int lotId, string state, string message);
}
