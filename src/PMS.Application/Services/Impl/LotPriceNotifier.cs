using Microsoft.Extensions.Logging;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Default <see cref="ILotPriceNotifier"/>: logs the notice, pushes it to the clients watching the lot (the same
/// public channel as the 3D map, so no plates and no personal data) and queues the bell notification for the
/// customers with an upcoming booking for that vehicle type at the lot.
/// </summary>
public class LotPriceNotifier(
    ILotRealtimeNotifier realtime = null,
    ILogger<LotPriceNotifier> logger = null,
    IUserNotificationPublisher notifications = null) : ILotPriceNotifier
{
    public async Task PriceChangedAsync(LotPriceNotice notice)
    {
        logger?.LogInformation("Lot {LotId} price changed: {Message}", notice.LotId, notice.Message);
        try
        {
            if (realtime != null)
                await realtime.LotPriceChangedAsync(notice.LotId, "Applied", notice.Message);
        }
        catch (Exception ex)
        {
            // Real-time is best effort; clients read the price again when they open the lot
            logger?.LogWarning(ex, "Could not push the price notice of lot {LotId}", notice.LotId);
        }

        try
        {
            notifications?.LotPriceChanged(LotPriceNotice.BookingTitle, notice.BookingMessage, notice.EventKey, notice.LotId, notice.VehicleTypeId, notice.LotName);
        }
        catch (Exception ex)
        {
            // The price change is already saved; losing the bell must not fail the Manager's request
            logger?.LogWarning(ex, "Could not queue the price notifications of lot {LotId}", notice.LotId);
        }
    }
}
