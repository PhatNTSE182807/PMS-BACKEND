using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Notifications;
using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>
/// A walk-in parked in the bay of a confirmed booking (BR-08, team rules of 2026-11-06). The case opens as soon as the
/// walk-in is recorded in that bay: the lot's Staff get an alert and the customer a notification. Staff then move the
/// booking to another free bay of its type (first hour free at check-in), or, only when none is free, cancel it: the
/// deposit goes back to the XXIV wallet and the lot pays the compensation of its pricing rule into the wallet too
/// (a "Compensation" payment the revenue report subtracts). A case closes on its own when the walk-in leaves first or
/// the booking ends another way; one still open when the hold ends is compensated by the system, never a no-show.
/// </summary>
public class BookedBayCaseService(
    PmsDbContext pmsContext,
    IPaymentService payments,
    ReservationService reservations,
    ParkingInventory inventory,
    LotScopeService lotScope,
    TimeProvider clock,
    IParkingRealtimeNotifier notifier = null,
    IUserNotificationPublisher notifications = null,
    ILayoutService layouts = null)
{
    public const string SystemActor = "System";

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /* Detection ------------------------------------------------------ */

    /// <summary>
    /// Opens a case when <paramref name="sessionId"/> is recorded in the bay of another confirmed booking. Returns null
    /// when the bay is not booked or a case is already open for that booking.
    /// </summary>
    public async Task<BookedBayCase> OpenIfBookedAsync(long sessionId)
    {
        var session = await pmsContext.ParkingSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session?.SlotId is not { } bayId || !SessionStatus.Open.Contains(session.Status))
            return null;

        var ownBooking = session.ReservationId ?? 0;
        var reservation = await pmsContext.Reservations
            .FirstOrDefaultAsync(r => r.ParkingLotId == session.ParkingLotId && r.SlotId == bayId
                                      && r.Status == ReservationStatus.Confirmed && r.Id != ownBooking);
        if (reservation == null
            || await pmsContext.BookedBayCases.AnyAsync(c => c.ReservationId == reservation.Id && c.Status == BookedBayCaseStatus.Open))
            return null;

        // The walk-in is physically there: the bay shows as taken (its space was counted when the walk-in entered)
        var bay = await pmsContext.Slots.FirstAsync(s => s.Id == bayId);
        inventory.MarkSlot(session.ParkingLotId, bay, SlotStatus.Occupied);

        var bayCase = new BookedBayCase
        {
            ParkingLotId = session.ParkingLotId,
            ReservationId = reservation.Id,
            SlotId = bayId,
            SessionId = session.Id,
            Status = BookedBayCaseStatus.Open,
            CreatedAt = Now
        };
        pmsContext.BookedBayCases.Add(bayCase);
        await pmsContext.SaveChangesAsync();

        var lot = await pmsContext.ParkingLots.AsNoTracking().FirstAsync(l => l.Id == session.ParkingLotId);
        var plate = session.HasPlate ? session.EntryPlateNumber : "A plate-less walk-in";
        var message = $"Walk-in {plate} parked in bay {bay.Code}, booked by {reservation.ReservationCode} (arrives {LocalTime(reservation.StartTime, lot)}). " +
                      "Move the booking in Exceptions & Alerts.";
        var (lotId, walkInId) = (session.ParkingLotId, session.Id);
        inventory.Queue(() => notifier?.OpsAlertAsync(lotId, "BOOKED_BAY_TAKEN", message, walkInId) ?? Task.CompletedTask);
        await inventory.FlushAsync();
        notifications?.BookedBayCaseChanged(bayCase.Id, NotificationTypes.BookedBayTaken);
        return bayCase;
    }

    /* Staff ---------------------------------------------------------- */

    /// <summary>Cases of the lot, open ones first; <paramref name="openOnly"/> keeps the open ones. Closes stale ones first.</summary>
    public async Task<IReadOnlyList<BookedBayCaseModel>> GetForLotAsync(int lotId, bool openOnly)
    {
        await lotScope.EnsureCanOperateAsync(lotId);
        await SweepAsync(lotId);

        var query = pmsContext.BookedBayCases.AsNoTracking().Where(c => c.ParkingLotId == lotId);
        if (openOnly)
            query = query.Where(c => c.Status == BookedBayCaseStatus.Open);
        var cases = await query
            .OrderByDescending(c => c.Status == BookedBayCaseStatus.Open)
            .ThenByDescending(c => c.CreatedAt)
            .Take(50)
            .ToListAsync();
        return await ToModelsAsync(cases);
    }

    /// <summary>Moves the booking to another free bay of its type; the customer gets the first hour free at check-in.</summary>
    public async Task<BookedBayCaseModel> ReassignAsync(long caseId, ReassignBookedBayModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var (bayCase, reservation) = await OpenCaseAsync(caseId);
        var target = (await FreeBaysAsync(reservation, bayCase.SlotId)).FirstOrDefault(s => s.Id == model.SlotId)
                     ?? throw new BadRequestException("That bay is not free or does not fit this vehicle. Pick another bay.");

        await ParkingInventory.WithSlotLockAsync([target.Id], async () =>
        {
            var newBay = await pmsContext.Slots.FirstAsync(s => s.Id == target.Id);
            if (newBay.Status != SlotStatus.Available)
                throw new ConflictException("That bay was just taken. Pick another bay.");

            // A booking that already held its bay moves the hold: its space was counted once, so the counter stays
            if (reservation.SlotHeldAt != null)
                inventory.MarkSlot(reservation.ParkingLotId, newBay, SlotStatus.Reserved);

            var oldCode = await BayCodeAsync(reservation.SlotId);
            pmsContext.ReservationChangeLogs.Add(new ReservationChangeLog
            {
                ReservationId = reservation.Id, Action = "SlotChanged", OldValue = oldCode, NewValue = newBay.Code,
                ChangedBy = lotScope.UserId, ChangedAt = Now
            });
            reservation.SlotId = newBay.Id;
            reservation.ZoneId = newBay.ZoneId;
            reservation.UpdatedAt = Now;

            bayCase.Status = BookedBayCaseStatus.Reassigned;
            bayCase.NewSlotId = newBay.Id;
            bayCase.ResolvedBy = lotScope.UserId;
            bayCase.ResolvedAt = Now;
            bayCase.ResolutionNote = Note(model.Note, $"Moved to bay {newBay.Code}.");
            await pmsContext.SaveChangesAsync();
            return true;
        });

        QueueReservation(reservation);
        await inventory.FlushAsync();
        notifications?.BookedBayCaseChanged(bayCase.Id, NotificationTypes.BookedBayReassigned);
        return (await ToModelsAsync([bayCase])).Single();
    }

    /// <summary>
    /// No free bay of the vehicle type is left: cancels the booking, refunds the deposit and pays the compensation,
    /// both into the customer's XXIV wallet. Refused while a bay is free (move the booking instead).
    /// </summary>
    public async Task<BookedBayCaseModel> CompensateAsync(long caseId, CompensateBookedBayModel model)
    {
        var (bayCase, reservation) = await OpenCaseAsync(caseId);
        var free = await FreeBaysAsync(reservation, bayCase.SlotId);
        if (free.Count > 0)
            throw new BadRequestException($"Bay {free[0].Code} is free: move the booking there instead.");

        await CancelAndCompensateAsync(bayCase, reservation, lotScope.UserId, model?.Note);
        return (await ToModelsAsync([bayCase])).Single();
    }

    /* Entry lane ------------------------------------------------------ */

    /// <summary>
    /// The booked customer reached the entry lane while a walk-in was in the bay (GateService.CheckInAsync): records
    /// the outcome. With <paramref name="newSlotId"/> the lane moved the booking; without, it parks as a walk-in, the
    /// lane already refunded <paramref name="depositRefunded"/> to the wallet and the compensation is paid here.
    /// </summary>
    public async Task ResolveAtGateAsync(Reservation reservation, long walkInSessionId, int bookedBayId, int? newSlotId, decimal depositRefunded)
    {
        var bayCase = await pmsContext.BookedBayCases
            .FirstOrDefaultAsync(c => c.ReservationId == reservation.Id && c.Status == BookedBayCaseStatus.Open);
        var opened = bayCase == null;
        if (opened)
        {
            bayCase = new BookedBayCase
            {
                ParkingLotId = reservation.ParkingLotId, ReservationId = reservation.Id, SlotId = bookedBayId,
                SessionId = walkInSessionId, Status = BookedBayCaseStatus.Open, CreatedAt = Now
            };
            pmsContext.BookedBayCases.Add(bayCase);
        }

        if (newSlotId != null)
        {
            bayCase.Status = BookedBayCaseStatus.Reassigned;
            bayCase.NewSlotId = newSlotId;
            bayCase.ResolvedBy = lotScope.UserId;
            bayCase.ResolvedAt = Now;
            bayCase.ResolutionNote = $"Moved to bay {await BayCodeAsync(newSlotId)} at the entry lane.";
            await pmsContext.SaveChangesAsync();
            notifications?.BookedBayCaseChanged(bayCase.Id, NotificationTypes.BookedBayReassigned);
            return;
        }

        await PayCompensationAsync(bayCase, reservation, lotScope.UserId, depositRefunded, "No bay was free at the entry lane: parked as a walk-in.");
        await pmsContext.SaveChangesAsync();
        await inventory.FlushAsync();
        if (bayCase.DepositRefunded > 0) notifications?.BookedBayCaseChanged(bayCase.Id, NotificationTypes.DepositRefunded);
        if (bayCase.CompensationPaid > 0) notifications?.BookedBayCaseChanged(bayCase.Id, NotificationTypes.CompensationPaid);
    }

    /// <summary>The booking was moved away from a taken bay before it arrived: its first hour is free (BR-08).</summary>
    public Task<bool> WasMovedAsync(long reservationId) =>
        pmsContext.BookedBayCases.AnyAsync(c => c.ReservationId == reservationId && c.Status == BookedBayCaseStatus.Reassigned);

    /* Timed work ----------------------------------------------------- */

    /// <summary>
    /// Settles open cases that need no Staff (minutely job, simulation tick, and before listing): the booking ended or
    /// moved another way, the walk-in left (the booking keeps its bay), or the hold ended while the bay was still taken
    /// (cancelled and compensated, never a no-show). Returns how many changed.
    /// </summary>
    public async Task<int> SweepAsync(int? lotId = null)
    {
        var open = await pmsContext.BookedBayCases
            .Where(c => c.Status == BookedBayCaseStatus.Open && (lotId == null || c.ParkingLotId == lotId))
            .ToListAsync();
        var changed = 0;
        foreach (var bayCase in open)
        {
            var reservation = await pmsContext.Reservations.FirstOrDefaultAsync(r => r.Id == bayCase.ReservationId);
            if (reservation is not { Status: ReservationStatus.Confirmed })
            {
                Close(bayCase, $"The booking is {reservation?.Status ?? "gone"}.");
            }
            else if (reservation.SlotId != bayCase.SlotId)
            {
                // The hold job moved the booking to a free bay on its own
                bayCase.Status = BookedBayCaseStatus.Reassigned;
                bayCase.NewSlotId = reservation.SlotId;
                bayCase.ResolvedBy = SystemActor;
                bayCase.ResolvedAt = Now;
                bayCase.ResolutionNote = $"Moved to bay {await BayCodeAsync(reservation.SlotId)} automatically.";
                await pmsContext.SaveChangesAsync();
                notifications?.BookedBayCaseChanged(bayCase.Id, NotificationTypes.BookedBayReassigned);
            }
            else if (!await pmsContext.ParkingSessions.AnyAsync(s => s.Id == bayCase.SessionId && s.SlotId == bayCase.SlotId && SessionStatus.Open.Contains(s.Status)))
            {
                // The walk-in left first: a booking that held the bay gets its hold back
                if (reservation.SlotHeldAt != null)
                {
                    var bay = await pmsContext.Slots.FirstAsync(s => s.Id == bayCase.SlotId);
                    if (bay.Status == SlotStatus.Available)
                        await inventory.HoldAsync(bay, bayCase.ParkingLotId);
                }
                Close(bayCase, "The walk-in left; the booking keeps its bay.");
                await pmsContext.SaveChangesAsync();
                notifications?.BookedBayCaseChanged(bayCase.Id, NotificationTypes.BookedBayFreed);
            }
            else if (reservation.EndTime <= Now)
            {
                await CancelAndCompensateAsync(bayCase, reservation, SystemActor, "The hold ended while the bay was still taken.");
            }
            else
            {
                continue;
            }
            changed++;
        }

        await pmsContext.SaveChangesAsync();
        await inventory.FlushAsync();
        return changed;
    }

    /* Helpers --------------------------------------------------------- */

    private async Task<(BookedBayCase Case, Reservation Reservation)> OpenCaseAsync(long caseId)
    {
        var bayCase = await pmsContext.BookedBayCases.FirstOrDefaultAsync(c => c.Id == caseId)
                      ?? throw new NotFoundException("Booked bay case not found.");
        await lotScope.EnsureCanOperateAsync(bayCase.ParkingLotId);
        await SweepAsync(bayCase.ParkingLotId);
        if (bayCase.Status != BookedBayCaseStatus.Open)
            throw new BadRequestException($"This case is already {bayCase.Status.ToLowerInvariant()}: {bayCase.ResolutionNote}");
        var reservation = await pmsContext.Reservations.FirstAsync(r => r.Id == bayCase.ReservationId);
        return (bayCase, reservation);
    }

    private async Task CancelAndCompensateAsync(BookedBayCase bayCase, Reservation reservation, string by, string note)
    {
        var bayCode = await BayCodeAsync(bayCase.SlotId);
        var reason = $"Booked bay {bayCode} was taken and no other bay was free";
        var held = await payments.DepositHeldAsync(reservation.Id);
        var refunded = await payments.RefundDepositAsync(reservation, held, reason, by, toWallet: true);

        // The held bay is the walk-in's now (Occupied): give the held space back to the zone ourselves
        if (reservation.SlotHeldAt != null)
        {
            var bay = await pmsContext.Slots.FirstAsync(s => s.Id == bayCase.SlotId);
            if (bay.Status != SlotStatus.Reserved)
            {
                await inventory.AdjustCounterAsync(bay.ZoneId, reservation.ParkingLotId, available: +1, occupied: 0);
                reservation.SlotHeldAt = null;
            }
        }
        await reservations.EndAsync(reservation, ReservationStatus.LotCancelled, reason, "Lot", by == SystemActor ? null : by);

        await PayCompensationAsync(bayCase, reservation, by, refunded, Note(note, "No other bay of this vehicle type was free: booking cancelled."));
        await pmsContext.SaveChangesAsync();
        QueueReservation(reservation);
        await inventory.FlushAsync();

        notifications?.BookedBayCaseChanged(bayCase.Id, NotificationTypes.BookingCancelledByLot);
        if (bayCase.DepositRefunded > 0) notifications?.BookedBayCaseChanged(bayCase.Id, NotificationTypes.DepositRefunded);
        if (bayCase.CompensationPaid > 0) notifications?.BookedBayCaseChanged(bayCase.Id, NotificationTypes.CompensationPaid);
    }

    private async Task PayCompensationAsync(BookedBayCase bayCase, Reservation reservation, string by, decimal depositRefunded, string note)
    {
        var bayCode = await BayCodeAsync(bayCase.SlotId);
        var amount = await CompensationForAsync(reservation, bayCase.SlotId);
        var payment = await payments.PayCompensationAsync(reservation, amount, $"booked bay {bayCode} taken ({reservation.ReservationCode})", by);

        bayCase.Status = BookedBayCaseStatus.Compensated;
        bayCase.DepositRefunded = depositRefunded;
        bayCase.CompensationPaid = payment?.Amount ?? 0;
        bayCase.CompensationPaymentId = payment?.Id;
        bayCase.ResolvedBy = by;
        bayCase.ResolvedAt = Now;
        bayCase.ResolutionNote = note;
    }

    /// <summary>The compensation of the lot's active pricing rule for the booked vehicle type (a lot rule before a shared one).</summary>
    public async Task<decimal> CompensationForAsync(Reservation reservation, int bookedBayId)
    {
        var vehicleTypeId = reservation.VehicleTypeId
                            ?? await pmsContext.Slots.Where(s => s.Id == bookedBayId).Select(s => (int?)s.VehicleTypeId).FirstOrDefaultAsync();
        if (vehicleTypeId == null)
            return 0;
        var now = Now;
        return await pmsContext.PricingRules.AsNoTracking()
            .Where(r => r.VehicleTypeId == vehicleTypeId && r.Status == "Active"
                        && (r.ParkingLotId == reservation.ParkingLotId || r.ParkingLotId == null)
                        && r.EffectiveFrom <= now && (r.EffectiveTo == null || r.EffectiveTo > now))
            .OrderByDescending(r => r.ParkingLotId != null)
            .ThenByDescending(r => r.Priority)
            .ThenByDescending(r => r.Id)
            .Select(r => r.BookedBayCompensation)
            .FirstOrDefaultAsync();
    }

    /// <summary>Free bays of the booked vehicle type the booking can move to: not occupied and not booked by anyone else.</summary>
    private async Task<List<Slot>> FreeBaysAsync(Reservation reservation, int bookedBayId)
    {
        var types = await inventory.VehicleTypesAsync();
        var vehicleTypeId = reservation.VehicleTypeId
                            ?? await pmsContext.Slots.Where(s => s.Id == bookedBayId).Select(s => s.VehicleTypeId).FirstAsync();
        var occupied = pmsContext.ParkingSessions.Where(s => s.SlotId != null && SessionStatus.Open.Contains(s.Status)).Select(s => s.SlotId);
        var booked = pmsContext.Reservations
            .Where(r => r.Id != reservation.Id && r.SlotId != null
                        && (r.Status == ReservationStatus.PendingDeposit || r.Status == ReservationStatus.Confirmed))
            .Select(r => r.SlotId);
        var free = await pmsContext.Slots.AsNoTracking().Include(s => s.Zone)
            .Where(s => s.Zone.ParkingLotId == reservation.ParkingLotId && s.Zone.IsActive && s.IsActive
                        && s.Status == SlotStatus.Available && s.Id != bookedBayId
                        && !occupied.Contains(s.Id) && !booked.Contains(s.Id))
            .ToListAsync();
        // Same bays as the entry lane keeps off: never one a waiting walk-in was sent to or where the 3D model draws a walk-in
        var kept = await KeptBays.ForAsync(pmsContext, layouts, reservation.ParkingLotId, reservation.Id);
        return free
            .Where(s => !kept.Contains(s.Id) && ParkingInventory.Compatible(types, s.VehicleTypeId, vehicleTypeId))
            .OrderBy(s => s.ZoneId == reservation.ZoneId ? 0 : 1)
            .ThenBy(s => s.Code, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<List<BookedBayCaseModel>> ToModelsAsync(List<BookedBayCase> cases)
    {
        if (cases.Count == 0)
            return [];

        var reservationIds = cases.Select(c => c.ReservationId).Distinct().ToList();
        var sessionIds = cases.Select(c => c.SessionId).Distinct().ToList();
        var bayIds = cases.Select(c => c.SlotId).Concat(cases.Where(c => c.NewSlotId != null).Select(c => c.NewSlotId!.Value)).Distinct().ToList();
        var bookings = await pmsContext.Reservations.AsNoTracking().Where(r => reservationIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id);
        var sessions = await pmsContext.ParkingSessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        var bays = await pmsContext.Slots.AsNoTracking().Where(s => bayIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        var customerIds = bookings.Values.Select(r => r.CustomerId).Distinct().ToList();
        var customers = await pmsContext.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.FullName);
        var vehicleIds = bookings.Values.Select(r => r.VehicleId).Distinct().ToList();
        var plates = await pmsContext.Vehicles.AsNoTracking().Where(v => vehicleIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.PlateNumber);
        var typeNames = await pmsContext.VehicleTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name);
        var resolverIds = cases.Where(c => c.ResolvedBy != null && c.ResolvedBy != SystemActor).Select(c => c.ResolvedBy).Distinct().ToList();
        var resolvers = await pmsContext.Employees.AsNoTracking().Where(e => resolverIds.Contains(e.UserId)).ToDictionaryAsync(e => e.UserId, e => e.FullName);

        var models = new List<BookedBayCaseModel>();
        foreach (var c in cases)
        {
            var booking = bookings.GetValueOrDefault(c.ReservationId);
            var walkIn = sessions.GetValueOrDefault(c.SessionId);
            var bay = bays.GetValueOrDefault(c.SlotId);
            var vehicleTypeId = booking?.VehicleTypeId ?? bay?.VehicleTypeId;
            var model = new BookedBayCaseModel
            {
                Id = c.Id,
                Status = c.Status,
                ParkingLotId = c.ParkingLotId,
                CreatedAt = Utc(c.CreatedAt),
                ResolvedAt = c.ResolvedAt is { } at ? Utc(at) : null,
                ResolvedByName = c.ResolvedBy == null ? null : c.ResolvedBy == SystemActor ? SystemActor : resolvers.GetValueOrDefault(c.ResolvedBy) ?? "Staff",
                ResolutionNote = c.ResolutionNote,
                ReservationId = c.ReservationId,
                ReservationCode = booking?.ReservationCode,
                ReservationStatus = booking?.Status,
                ArrivalTime = booking == null ? default : Utc(booking.StartTime),
                HoldEndsAt = booking == null ? default : Utc(booking.EndTime),
                CustomerName = booking == null ? null : customers.GetValueOrDefault(booking.CustomerId),
                BookedPlate = booking == null ? null : plates.GetValueOrDefault(booking.VehicleId),
                VehicleTypeId = vehicleTypeId,
                VehicleTypeName = vehicleTypeId is { } typeId ? typeNames.GetValueOrDefault(typeId) : null,
                BayId = c.SlotId,
                BayCode = bay?.Code,
                NewBayId = c.NewSlotId,
                NewBayCode = c.NewSlotId is { } newId ? bays.GetValueOrDefault(newId)?.Code : null,
                WalkInSessionId = c.SessionId,
                WalkInSessionCode = walkIn?.SessionCode,
                WalkInPlate = walkIn == null ? null : walkIn.HasPlate ? walkIn.EntryPlateNumber : "No plate",
                WalkInEntryTime = walkIn == null ? default : Utc(walkIn.EntryTime),
                WalkInInside = walkIn != null && SessionStatus.Open.Contains(walkIn.Status),
                DepositRefunded = c.DepositRefunded,
                CompensationPaid = c.CompensationPaid
            };

            if (c.Status == BookedBayCaseStatus.Open && booking != null)
            {
                model.DepositHeld = await payments.DepositHeldAsync(booking.Id);
                model.CompensationAmount = await CompensationForAsync(booking, c.SlotId);
                model.FreeBays = (await FreeBaysAsync(booking, c.SlotId))
                    .Select(s => new BookedBayOptionModel { SlotId = s.Id, Code = s.Code, ZoneName = s.Zone?.Name })
                    .ToList();
            }
            models.Add(model);
        }
        return models;
    }

    private void Close(BookedBayCase bayCase, string note)
    {
        bayCase.Status = BookedBayCaseStatus.Closed;
        bayCase.ResolvedBy = SystemActor;
        bayCase.ResolvedAt = Now;
        bayCase.ResolutionNote = note;
    }

    private async Task<string> BayCodeAsync(int? slotId) =>
        slotId == null ? null : await pmsContext.Slots.Where(s => s.Id == slotId).Select(s => s.Code).FirstOrDefaultAsync();

    private void QueueReservation(Reservation r)
    {
        var (userId, id, lotId, status, slotId) = (r.UserId, r.Id, r.ParkingLotId, r.Status, r.SlotId);
        inventory.Queue(() => notifier?.ReservationChangedAsync(userId, id, lotId, status, slotId) ?? Task.CompletedTask);
    }

    private static string Note(string typed, string fallback) =>
        string.IsNullOrWhiteSpace(typed) ? fallback : typed.Trim().Length > 500 ? typed.Trim()[..500] : typed.Trim();

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>"14:30" in the lot's time zone (Vietnam by default).</summary>
    private static string LocalTime(DateTime utc, ParkingLot lot) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), ParkingText.FindTimeZone(lot.TimeZoneId))
            .ToString("HH:mm", CultureInfo.InvariantCulture);
}
