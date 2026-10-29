using System;
using System.Linq;
using System.Linq.Expressions;
using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>Total and occupied bays of one parking lot.</summary>
public sealed class LotBaysRow
{
    public int LotId { get; init; }
    public int Total { get; init; }
    public int Occupied { get; init; }
}

/// <summary>Settled revenue of one parking lot, today and since the start of the month.</summary>
public sealed class LotRevenueRow
{
    public int LotId { get; init; }
    public decimal MonthToDate { get; init; }
    public decimal Today { get; init; }
}

/// <summary>
/// The set-based queries behind the Admin dashboard. Each one is a single GROUP BY that covers every lot, so
/// the number of SQL statements does not depend on how many lots exist. Kept apart from the service so a
/// test can print their SQL (the in-memory provider would hide a query that SQL Server cannot translate).
/// </summary>
public static class AdminDashboardQueries
{
    /// <summary>Sessions whose fee has been collected and counts as revenue (same rule as the revenue report).</summary>
    public static readonly Expression<Func<ParkingSession, bool>> Settled = s =>
        (s.Status == SessionStatus.Completed && s.ExitTime != null)
        || (s.Status == SessionStatus.Paid && s.ExitTime == null && s.UpdatedAt != null);

    /// <summary>
    /// Bays per lot from the zone counters of the active zones of lots that are not deleted. The counter is the
    /// live source: it also counts walk-in vehicles, which have no slot.
    /// </summary>
    public static IQueryable<LotBaysRow> BaysFromCounters(this PmsDbContext db) =>
        db.ZoneCounters
            .Where(c => c.Zone.IsActive && !c.Zone.ParkingLot.IsDeleted)
            .GroupBy(c => c.Zone.ParkingLotId)
            .Select(g => new LotBaysRow
            {
                LotId = g.Key,
                Total = g.Sum(c => c.TotalCapacity),
                Occupied = g.Sum(c => c.OccupiedSpaces)
            });

    /// <summary>
    /// Bays per lot from the active slots of active zones that have no counter yet (the fallback when a zone has
    /// never been counted): every active slot is a bay, an Occupied slot is a used one.
    /// </summary>
    public static IQueryable<LotBaysRow> BaysFromSlotsWithoutCounter(this PmsDbContext db) =>
        db.Slots
            .Where(s => s.IsActive && s.Zone.IsActive && !s.Zone.ParkingLot.IsDeleted && s.Zone.ZoneCounter == null)
            .GroupBy(s => s.Zone.ParkingLotId)
            .Select(g => new LotBaysRow
            {
                LotId = g.Key,
                Total = g.Count(),
                Occupied = g.Count(s => s.Status == SlotStatus.Occupied)
            });

    /// <summary>
    /// Revenue per lot for [monthStart, tomorrowStart), split into today and the whole month. The settled time
    /// (ExitTime of a completed session, UpdatedAt of a paid one) is projected first so one pass sums both periods.
    /// </summary>
    public static IQueryable<LotRevenueRow> RevenueByLot(
        this PmsDbContext db, DateTime monthStartUtc, DateTime todayStartUtc, DateTime tomorrowStartUtc) =>
        db.ParkingSessions
            .Where(Settled)
            .Select(s => new
            {
                s.ParkingLotId,
                s.FinalFee,
                SettledAt = s.Status == SessionStatus.Completed ? s.ExitTime : s.UpdatedAt
            })
            .Where(s => s.SettledAt >= monthStartUtc && s.SettledAt < tomorrowStartUtc)
            .GroupBy(s => s.ParkingLotId)
            .Select(g => new LotRevenueRow
            {
                LotId = g.Key,
                MonthToDate = g.Sum(s => s.FinalFee),
                Today = g.Sum(s => s.SettledAt >= todayStartUtc ? s.FinalFee : 0m)
            });

    /// <summary>All-time settled revenue of the lots that still exist.</summary>
    public static IQueryable<ParkingSession> SettledSessionsOfLiveLots(this PmsDbContext db) =>
        db.ParkingSessions.Where(s => !s.ParkingLot.IsDeleted).Where(Settled);
}
