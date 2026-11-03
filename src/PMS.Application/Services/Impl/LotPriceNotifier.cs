using Microsoft.Extensions.Logging;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Default <see cref="ILotPriceNotifier"/>: logs the notice and pushes it to the clients watching the lot
/// (the same public channel as the 3D map, so no plates and no personal data).
/// </summary>
public class LotPriceNotifier(ILotRealtimeNotifier realtime = null, ILogger<LotPriceNotifier> logger = null) : ILotPriceNotifier
{
    public Task PriceAppliedAsync(LotPriceNotice notice) => PushAsync(notice, "Applied", notice.AppliedMessage);

    public Task PriceScheduledAsync(LotPriceNotice notice) => PushAsync(notice, "Scheduled", notice.ScheduledMessage);

    private async Task PushAsync(LotPriceNotice notice, string state, string message)
    {
        logger?.LogInformation("Lot {LotId} price {State}: {Message}", notice.LotId, state, message);
        try
        {
            if (realtime != null)
                await realtime.LotPriceChangedAsync(notice.LotId, state, message);
        }
        catch (Exception ex)
        {
            // Real-time is best effort; clients read the price again when they open the lot
            logger?.LogWarning(ex, "Could not push the price notice of lot {LotId}", notice.LotId);
        }
    }
}
