using Microsoft.EntityFrameworkCore;
using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence;

namespace PMS.Application.Helpers.Parking;

/// <summary>
/// When each vehicle parked in a lot is expected to leave its bay, so a booking for later can take a bay that is
/// occupied now (team rule of 2026-11-17). A vehicle on a scheduled booking leaves at its planned departure (it is
/// towed soon after if it stays); a simulated walk-in leaves at its planned exit. A real walk-in or a pay-as-you-go
/// booking has no planned exit, so nobody knows when its bay turns free.
/// </summary>
public static class BayForecast
{
    /// <summary>A vehicle in a bay now and when it is expected to drive out (null: unknown).</summary>
    public sealed record ParkedVehicle(int SlotId, DateTime? ExpectedLeave, bool OnBooking);

    /// <summary>Every vehicle parked in a bay of <paramref name="lotId"/> (or only in <paramref name="slotId"/>).</summary>
    public static async Task<List<ParkedVehicle>> ParkedAsync(PmsDbContext context, int lotId, int? slotId = null)
    {
        var rows = await context.ParkingSessions.AsNoTracking()
            .Where(s => s.SlotId != null && s.ParkingLotId == lotId && SessionStatus.Open.Contains(s.Status)
                        && (slotId == null || s.SlotId == slotId))
            .Select(s => new
            {
                SlotId = s.SlotId!.Value,
                s.IsSimulated,
                s.SimulatedExitAt,
                OnBooking = s.ReservationId != null,
                Departure = context.Reservations
                    .Where(r => r.Id == s.ReservationId && r.BookingMode == BookingMode.Scheduled)
                    .Select(r => r.DepartureTime)
                    .FirstOrDefault()
            })
            .ToListAsync();
        return rows
            .Select(r => new ParkedVehicle(r.SlotId, r.Departure ?? (r.IsSimulated ? r.SimulatedExitAt : null), r.OnBooking))
            .ToList();
    }

    /// <summary>
    /// When the bay of <paramref name="vehicle"/> is ready for the next booking: a booked vehicle's bay after the
    /// turnover gap (the tow happens inside it), a simulated walk-in's bay right at its exit. Null when unknown.
    /// </summary>
    public static DateTime? FreeFrom(ParkedVehicle vehicle, TimeSpan turnover, DateTime now)
    {
        if (vehicle.ExpectedLeave is not { } leave)
            return null;
        // A vehicle already past its exit is gone on the next run (or towed): count from now
        var from = leave < now ? now : leave;
        return vehicle.OnBooking ? from.Add(turnover) : from;
    }
}
