using System.Threading.Tasks;
using PMS.Application.Models.Layout;

namespace PMS.Application.Services;

/// <summary>
/// Pushes lot changes to the clients watching that lot (SignalR in PMS.API).
/// Public payloads carry ids and statuses only, never plates.
/// </summary>
public interface ILotRealtimeNotifier
{
    Task SlotStatusChangedAsync(int lotId, int slotId, string status);

    Task LotStatusChangedAsync(int lotId, string status);

    /// <summary>The lot's 3D layout was (re)generated, so clients reload it.</summary>
    Task LayoutChangedAsync(int lotId);

    /// <summary>The new state of the bays and zones a saved change touched ("LotLiveChanged"), so maps patch in place.</summary>
    Task LotLiveChangedAsync(LotLiveChangesModel changes);

    /// <summary>A price of the lot is now in force (state "Applied").</summary>
    Task LotPriceChangedAsync(int lotId, string state, string message);
}
