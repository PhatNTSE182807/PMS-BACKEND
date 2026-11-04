using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Optimistic concurrency for everything that decides on the state of a lot (vehicles inside, price waiting).
/// Every such change saves the lot's row, whose <c>RowVersion</c> is a concurrency token. When two changes
/// started from the same state, the later save throws <see cref="DbUpdateConcurrencyException"/> and nothing of
/// it is written; <see cref="RunAsync{T}"/> then undoes what the attempt staged and runs it again, so the second
/// change decides on the state the first one left. Typical case: the last vehicle leaves while another enters,
/// or a Manager sets a price while the last vehicle leaves.
/// </summary>
public static class LotConcurrency
{
    /// <summary>
    /// Each round lets one contender through, so a vehicle needs as many rounds as there are others racing it;
    /// a lot with several lanes and a Manager's request can easily be six at once.
    /// </summary>
    public const int MaxAttempts = 10;

    /// <summary>What an attempt stages; only these are undone before a retry, so unrelated pending changes survive.</summary>
    private static readonly Type[] StagedTypes =
        [typeof(ParkingLot), typeof(ParkingSession), typeof(PendingPriceChange), typeof(PricingRule), typeof(PricingRuleHistory)];

    /// <summary>
    /// Runs <paramref name="attempt"/>, which must read the lot first, stage its changes and save once.
    /// It may run several times, so it must not do anything that cannot be repeated before that save.
    /// </summary>
    public static async Task<T> RunAsync<T>(PmsDbContext db, Func<Task<T>> attempt)
    {
        for (var tries = 1; ; tries++)
        {
            try
            {
                return await attempt();
            }
            catch (DbUpdateConcurrencyException) when (tries < MaxAttempts)
            {
                // Another request saved this lot between our read and our save: forget our attempt, read again
                await DiscardStagedChangesAsync(db);
                await Task.Delay(Random.Shared.Next(10, 40) * tries);
            }
            catch (DbUpdateConcurrencyException)
            {
                await DiscardStagedChangesAsync(db);
                throw new ConflictException("The parking lot is busy right now. Please try again.");
            }
        }
    }

    /// <summary>New rows are dropped; changed or removed ones are reloaded, which also refreshes the lot's RowVersion.</summary>
    private static async Task DiscardStagedChangesAsync(PmsDbContext db)
    {
        var staged = db.ChangeTracker.Entries()
            .Where(e => e.State != EntityState.Unchanged && e.State != EntityState.Detached && StagedTypes.Contains(e.Entity.GetType()))
            .ToList();

        foreach (var entry in staged)
        {
            if (entry.State == EntityState.Added)
                entry.State = EntityState.Detached;
            else
                await entry.ReloadAsync();
        }
    }

    /// <summary>
    /// Makes the save write the lot's row even when no value of it changed, so the RowVersion check applies.
    /// (SQL Server bumps the version on every UPDATE of the row.)
    /// </summary>
    public static void Touch(PmsDbContext db, ParkingLot lot) =>
        db.Entry(lot).Property(l => l.OccupiedBays).IsModified = true;
}
