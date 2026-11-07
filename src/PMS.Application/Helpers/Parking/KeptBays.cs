using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.Layout;
using PMS.Application.Models.Operations;
using PMS.Application.Services;
using PMS.DataAccess.Persistence;

namespace PMS.Application.Helpers.Parking;

/// <summary>
/// Free bays that must not be handed to another vehicle: a bay booked for a customer (now or later), a bay a
/// simulated walk-in waiting at the entry lane was sent to, and a bay where the 3D model draws a walk-in that has
/// no bay of its own. Used when the simulation parks walk-ins and when the entry lane moves a booking off a taken bay.
/// </summary>
public static class KeptBays
{
    private static readonly string[] ActiveBookings = [ReservationStatus.PendingDeposit, ReservationStatus.Confirmed];

    /// <param name="exceptReservationId">The booking being placed: its own bay does not count against it.</param>
    public static async Task<HashSet<int>> ForAsync(PmsDbContext db, ILayoutService layouts, int lotId, long? exceptReservationId = null)
    {
        var booked = await db.Reservations.AsNoTracking()
            .Where(r => r.ParkingLotId == lotId && r.SlotId != null && ActiveBookings.Contains(r.Status)
                        && (exceptReservationId == null || r.Id != exceptReservationId))
            .Select(r => r.SlotId!.Value)
            .ToListAsync();
        var queued = await db.GateRequests.AsNoTracking()
            .Where(g => g.ParkingLotId == lotId && g.IsSimulated && g.Kind == GateRequestKind.CheckIn
                        && g.Status == GateRequestStatus.Waiting && g.SlotId != null)
            .Select(g => g.SlotId!.Value)
            .ToListAsync();

        var kept = booked.Concat(queued).ToHashSet();
        kept.UnionWith(await CountedWalkInBaysAsync(layouts, lotId));
        return kept;
    }

    /// <summary>
    /// Bays where the 3D model draws the walk-ins counted in a zone without a bay of their own: the zone counter's
    /// vehicles beyond the bays already taken, in free bays without a booking first, then by level, row and column
    /// (the same rule as walkInBays in the frontend's src/lib/lot-layout.ts).
    /// </summary>
    public static async Task<HashSet<int>> CountedWalkInBaysAsync(ILayoutService layouts, int lotId)
    {
        if (layouts == null)
            return [];
        Layout2DResponseModel plan;
        try
        {
            plan = await layouts.Get2DAsync(lotId, LayoutDetail.Slots);
        }
        catch (NotFoundException)
        {
            // No published layout: the model draws no bay, so no bay is taken on it
            return [];
        }

        var cells = plan.Floors
            .SelectMany((floor, index) => (floor.Cells ?? []).Select(cell => (Cell: cell, Floor: index)))
            .ToList();
        var drawn = new HashSet<int>();
        foreach (var zone in plan.ZoneCounters ?? [])
        {
            var inZone = cells.Where(c => c.Cell.ZoneId == zone.ZoneId).ToList();
            var shown = inZone.Count(c => c.Cell.Status != SlotStatus.Available);
            var missing = Math.Max(0, zone.Total - zone.Free - shown);
            var taken = inZone.Where(c => c.Cell.Status == SlotStatus.Available)
                .OrderBy(c => c.Cell.BookedLater).ThenBy(c => c.Floor).ThenBy(c => c.Cell.Row).ThenBy(c => c.Cell.Col)
                .Take(missing);
            foreach (var c in taken)
                drawn.Add(c.Cell.SlotId);
        }
        return drawn;
    }
}
