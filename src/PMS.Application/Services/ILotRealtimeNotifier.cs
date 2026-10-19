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
}
