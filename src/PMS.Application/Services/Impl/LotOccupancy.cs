using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>
/// The one place that counts vehicles in and out of a lot (<see cref="ParkingLot.OccupiedBays"/>): every session
/// that opens goes through <see cref="StageEntryAsync"/> and every session that ends (check-out, abnormal close,
/// turned around at the barrier) through <see cref="ReleaseAsync"/>. When the last vehicle leaves, the prices
/// that were waiting for an empty lot become live in the same save.
/// </summary>
public class LotOccupancy(
    PmsDbContext db,
    DeferredPriceService prices,
    ILotPriceNotifier priceNotifier = null,
    ILogger<LotOccupancy> logger = null)
{
    /// <summary>
    /// Entry: counts the vehicle in. Call it inside <see cref="LotConcurrency.RunAsync{T}"/>, <b>before</b> reading
    /// the tariff and in the same save as the new session. Reading the lot first remembers its RowVersion, so if a
    /// price becomes live (or another vehicle enters) before our save, the save fails and the whole entry is
    /// redone with the new price instead of recording a session with a stale one.
    /// </summary>
    public async Task StageEntryAsync(int lotId)
    {
        var lot = await db.ParkingLots.FirstAsync(l => l.Id == lotId);
        lot.OccupiedBays++;
    }

    /// <summary>
    /// Exit: counts <paramref name="vehicles"/> out, and when the lot is then empty makes the waiting prices live.
    /// Call it after the session change itself was saved. It never throws: the vehicle has already left, so a
    /// failure here is logged and <see cref="SettleIdleLotsAsync"/> puts the lot right on its next run.
    /// </summary>
    public async Task ReleaseAsync(int lotId, int vehicles = 1)
    {
        try
        {
            var notices = await LotConcurrency.RunAsync(db, async () =>
            {
                // Concurrency: two exits (or an exit and an entry or a price request) that read the same
                // OccupiedBays cannot both save. The loser throws DbUpdateConcurrencyException inside
                // SaveChangesAsync, RunAsync discards this attempt and calls us again, and we re-read the lot:
                // the count is then right, and only the exit that really sees 0 makes the prices live.
                var lot = await db.ParkingLots.FirstOrDefaultAsync(l => l.Id == lotId);
                if (lot == null)
                    return (IReadOnlyList<LotPriceNotice>)[];

                lot.OccupiedBays = Math.Max(0, lot.OccupiedBays - vehicles);
                LotConcurrency.Touch(db, lot);

                var promoted = lot.OccupiedBays == 0 ? await prices.StagePromotionAsync(lot) : [];
                await db.SaveChangesAsync();
                return promoted;
            });

            await NotifyAppliedAsync(notices);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Could not count {Vehicles} vehicle(s) out of lot {LotId}; the idle-lot job will settle it", vehicles, lotId);
        }
    }

    /// <summary>
    /// Safety net, run by a job: a lot with a price waiting and no vehicle really inside gets its price (and a
    /// correct counter) even if an exit above could not finish. "Really inside" is counted from the sessions,
    /// not from the counter. Returns the number of lots settled.
    /// </summary>
    public async Task<int> SettleIdleLotsAsync()
    {
        var lotIds = await db.PendingPriceChanges.AsNoTracking().Select(p => p.ParkingLotId).Distinct().ToListAsync();

        var settled = 0;
        foreach (var lotId in lotIds)
        {
            var notices = await LotConcurrency.RunAsync(db, async () =>
            {
                var lot = await db.ParkingLots.FirstOrDefaultAsync(l => l.Id == lotId);
                if (lot == null)
                    return (IReadOnlyList<LotPriceNotice>)[];

                // Counted after the lot was read: an entry saved after this count changes the lot's row, so our save fails and we count again
                var inside = await db.ParkingSessions.CountAsync(s => s.ParkingLotId == lotId && SessionStatus.Open.Contains(s.Status));
                if (inside > 0)
                    return [];

                lot.OccupiedBays = 0;
                LotConcurrency.Touch(db, lot);
                var promoted = await prices.StagePromotionAsync(lot);
                await db.SaveChangesAsync();
                return promoted;
            });

            if (notices.Count > 0)
                settled++;
            await NotifyAppliedAsync(notices);
        }

        return settled;
    }

    private async Task NotifyAppliedAsync(IReadOnlyList<LotPriceNotice> notices)
    {
        if (priceNotifier == null)
            return;

        foreach (var notice in notices)
        {
            try
            {
                await priceNotifier.PriceAppliedAsync(notice);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not announce the new price of lot {LotId}", notice.LotId);
            }
        }
    }
}
