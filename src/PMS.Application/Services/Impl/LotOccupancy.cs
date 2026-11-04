using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>
/// The one place that counts vehicles in and out of a lot (<see cref="ParkingLot.OccupiedBays"/>): every session
/// that opens goes through <see cref="StageEntryAsync"/> and every session that ends (check-out, abnormal close,
/// turned around at the barrier) through <see cref="ReleaseAsync"/>. A price change saves the same row, so an entry
/// can never record a session with a stale price.
/// </summary>
public class LotOccupancy(PmsDbContext db, ILogger<LotOccupancy> logger = null)
{
    /// <summary>
    /// Entry: counts the vehicle in. Call it inside <see cref="LotConcurrency.RunAsync{T}"/>, <b>before</b> reading
    /// the tariff and in the same save as the new session. Reading the lot first remembers its RowVersion, so if a
    /// price changes (or another vehicle enters) before our save, the save fails and the whole entry is
    /// redone with the new price instead of recording a session with a stale one.
    /// </summary>
    public async Task StageEntryAsync(int lotId)
    {
        var lot = await db.ParkingLots.FirstAsync(l => l.Id == lotId);
        lot.OccupiedBays++;
    }

    /// <summary>
    /// Exit: counts <paramref name="vehicles"/> out. Call it after the session change itself was saved. It never
    /// throws: the vehicle has already left, so a failure here is only logged.
    /// </summary>
    public async Task ReleaseAsync(int lotId, int vehicles = 1)
    {
        try
        {
            await LotConcurrency.RunAsync(db, async () =>
            {
                // Concurrency: two exits (or an exit and an entry or a price request) that read the same
                // OccupiedBays cannot both save. The loser throws DbUpdateConcurrencyException inside
                // SaveChangesAsync, RunAsync discards this attempt and calls us again, and we re-read the lot.
                var lot = await db.ParkingLots.FirstOrDefaultAsync(l => l.Id == lotId);
                if (lot == null)
                    return false;

                lot.OccupiedBays = Math.Max(0, lot.OccupiedBays - vehicles);
                LotConcurrency.Touch(db, lot);
                await db.SaveChangesAsync();
                return true;
            });
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Could not count {Vehicles} vehicle(s) out of lot {LotId}", vehicles, lotId);
        }
    }
}
