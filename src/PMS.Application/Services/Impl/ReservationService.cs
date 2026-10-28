using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using PMS.Application.Models.VehicleDocuments;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <inheritdoc />
public class ReservationService(
    PmsDbContext pmsContext,
    ParkingOptions options,
    ParkingInventory inventory,
    IPaymentService paymentService,
    ParkingTokenSigner signer,
    LotScopeService lotScope,
    TimeProvider clock,
    IParkingRealtimeNotifier notifier = null) : IReservationService
{
    private ReservationRules Rules => options.Reservations;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<ReservationResponseModel> CreateAsync(string userId, CreateReservationModel model)
    {
        var customer = await CustomerAsync(userId);
        var (arrival, departure, mode) = ValidateTimes(model.BookingMode, model.ArrivalTime, model.DepartureTime);

        var lot = await pmsContext.ParkingLots.AsNoTracking().FirstOrDefaultAsync(p => p.Id == model.ParkingLotId && !p.IsDeleted)
                  ?? throw new NotFoundException("Parking lot not found.");
        if (lot.Status != LotStatus.Active)
            throw new BadRequestException("The lot is not taking reservations right now.");
        // Nobody would be there to receive the car: a lot needs an active Manager and Staff before it takes bookings
        if (!await LotStaffing.IsStaffedAsync(pmsContext, lot.Id))
            throw new BadRequestException("This lot is under maintenance: it has no Manager and Staff on duty yet, so it cannot take bookings.");

        var vehicle = await pmsContext.Vehicles.AsNoTracking()
                          .FirstOrDefaultAsync(v => v.Id == model.VehicleId && v.CustomerId == customer.Id && v.IsActive)
                      ?? throw new BadRequestException("This vehicle is not on your account.");

        // Only a vehicle whose papers Staff approved can be booked
        var papers = await pmsContext.VehicleVerifications.AsNoTracking()
            .Where(v => v.VehicleId == vehicle.Id)
            .OrderByDescending(v => v.CreatedAt)
            .Select(v => v.Status)
            .FirstOrDefaultAsync();
        if (papers == null || !VehicleVerificationStatus.Verified.Contains(papers))
            throw new BadRequestException("This vehicle is waiting for Staff to approve its papers and cannot be booked yet.");

        var types = await inventory.VehicleTypesAsync();
        var vehicleType = types.GetValueOrDefault(vehicle.VehicleTypeId);
        if (vehicleType == null || !Rules.ReservableVehicleTypeCodes.Contains(vehicleType.Code, StringComparer.OrdinalIgnoreCase))
            throw new BadRequestException("This vehicle type cannot be reserved.");

        // Per account and per plate, so a double click or a second tab cannot slip past BR-07
        var reservation = await WithBookingLockAsync([$"customer:{customer.Id}", $"plate:{vehicle.PlateNormalized}"], () =>
            ParkingInventory.WithSlotLockAsync([model.SlotId], async () =>
        {
            await EnsureBookingLimitsAsync(customer.Id, vehicle);

            // A booking close to arrival locks the bay now; one further ahead only books its time window
            var holdNow = ShouldHoldNow(arrival);
            var slot = await LoadSlotForLotAsync(model.SlotId, lot.Id, mustBeFreeNow: holdNow);
            if (!ParkingInventory.Compatible(types, slot.VehicleTypeId, vehicle.VehicleTypeId))
                throw new BadRequestException("This slot is for another vehicle type.");
            await EnsureSlotFreeForAsync(slot.Id, arrival, departure, null);

            if (holdNow)
                await inventory.HoldAsync(slot, lot.Id);

            // With a deposit the booking waits for the payment and then holds the slot 30 minutes after
            // arrival; without one it is confirmed at once and holds the slot only 15 minutes.
            var withDeposit = model.PayDeposit;
            var created = new Reservation
            {
                ReservationCode = ParkingText.NewCode("RSV", Now),
                CustomerId = customer.Id,
                UserId = userId,
                VehicleId = vehicle.Id,
                VehicleTypeId = vehicle.VehicleTypeId,
                ParkingLotId = lot.Id,
                ZoneId = slot.ZoneId,
                SlotId = slot.Id,
                StartTime = arrival,
                EndTime = arrival.AddMinutes(Rules.HoldMinutesFor(withDeposit)),
                BookingMode = mode,
                DepartureTime = departure,
                SlotHeldAt = holdNow ? Now : null,
                DepositAmount = withDeposit ? DepositFor(vehicleType.Code) : 0,
                DepositDueAt = withDeposit ? Now.AddMinutes(Rules.DepositMinutes) : null,
                Status = withDeposit ? ReservationStatus.PendingDeposit : ReservationStatus.Confirmed,
                ConfirmedAt = withDeposit ? null : Now,
                CreatedAt = Now
            };
            pmsContext.Reservations.Add(created);
            await pmsContext.SaveChangesAsync();

            Log(created.Id, "Created", null, $"{slot.Code} {mode} @ {arrival:O}{(departure != null ? $" - {departure:O}" : "")}{(withDeposit ? "" : " no deposit")}", userId);
            await pmsContext.SaveChangesAsync();
            return created;
        }));

        QueueReservationPush(reservation);
        await inventory.FlushAsync();
        return await ToModelAsync(reservation);
    }

    public async Task<IReadOnlyList<ReservationResponseModel>> GetMineAsync(string userId, bool activeOnly)
    {
        var query = pmsContext.Reservations.AsNoTracking().Where(r => r.UserId == userId);
        if (activeOnly)
            query = query.Where(r => r.Status == ReservationStatus.PendingDeposit || r.Status == ReservationStatus.Confirmed || r.Status == ReservationStatus.CheckedIn);

        var reservations = await query.OrderByDescending(r => r.StartTime).Take(100).ToListAsync();
        return await ToModelsAsync(reservations);
    }

    public async Task<ReservationResponseModel> GetAsync(string userId, long id) =>
        await ToModelAsync(await OwnAsync(userId, id, tracked: false));

    public async Task<ReservationResponseModel> UpdateAsync(string userId, long id, UpdateReservationModel model)
    {
        var reservation = await OwnAsync(userId, id, tracked: true);
        ParkingStateMachines.EnsureReservation(reservation.Status, ReservationStatus.Confirmed);
        if (reservation.Status != ReservationStatus.Confirmed)
            throw new BadRequestException("Only confirmed reservations can be changed.");
        if (Now > reservation.StartTime.AddMinutes(-Rules.CancelRefundMinutes))
            throw new BadRequestException($"Changes close {Rules.CancelRefundMinutes} minutes before arrival; the deadline has passed.");

        // The new window: anything not sent keeps its current value; pay-as-you-go has no departure
        var mode = model.BookingMode ?? reservation.BookingMode ?? BookingMode.PayAsYouGo;
        var keptDeparture = reservation.DepartureTime is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)) : (DateTimeOffset?)null;
        var (arrival, departure, newMode) = ValidateTimes(mode,
            model.ArrivalTime ?? new DateTimeOffset(DateTime.SpecifyKind(reservation.StartTime, DateTimeKind.Utc)),
            model.DepartureTime ?? keptDeparture);

        var oldSlotId = reservation.SlotId;
        var targetSlotId = model.SlotId ?? oldSlotId
                           ?? throw new BadRequestException("This reservation has no bay to change.");
        var types = await inventory.VehicleTypesAsync();

        await ParkingInventory.WithSlotLockAsync(new[] { targetSlotId, oldSlotId ?? targetSlotId }, async () =>
        {
            var holdNow = ShouldHoldNow(arrival);
            var slotChanged = targetSlotId != oldSlotId;
            var slot = await LoadSlotForLotAsync(targetSlotId, reservation.ParkingLotId,
                mustBeFreeNow: holdNow && (slotChanged || reservation.SlotHeldAt == null));
            if (!ParkingInventory.Compatible(types, slot.VehicleTypeId, reservation.VehicleTypeId ?? slot.VehicleTypeId))
                throw new BadRequestException("This slot is for another vehicle type.");
            await EnsureSlotFreeForAsync(slot.Id, arrival, departure, reservation.Id);

            // Give the old bay back when the booking moves or is no longer close enough to lock it
            if (reservation.SlotHeldAt != null && (slotChanged || !holdNow))
            {
                await inventory.ReleaseHoldAsync(oldSlotId, reservation.ParkingLotId);
                reservation.SlotHeldAt = null;
            }
            if (slotChanged)
            {
                Log(reservation.Id, "SlotChanged", oldSlotId?.ToString(), slot.Code, userId);
                reservation.SlotId = slot.Id;
                reservation.ZoneId = slot.ZoneId;
            }
            if (holdNow && reservation.SlotHeldAt == null)
            {
                await inventory.HoldAsync(slot, reservation.ParkingLotId);
                reservation.SlotHeldAt = Now;
            }

            if (arrival != reservation.StartTime)
            {
                Log(reservation.Id, "ArrivalChanged", reservation.StartTime.ToString("O"), arrival.ToString("O"), userId);
                reservation.StartTime = arrival;
                // Keep the hold length of the booking type: 30 minutes with a deposit, 15 without
                reservation.EndTime = arrival.AddMinutes(Rules.HoldMinutesFor(reservation.DepositAmount > 0));
                reservation.ReminderSentAt = null;
                reservation.HoldReminderSentAt = null;
            }
            if (newMode != reservation.BookingMode || departure != reservation.DepartureTime)
            {
                Log(reservation.Id, "WindowChanged", $"{reservation.BookingMode} {reservation.DepartureTime:O}", $"{newMode} {departure:O}", userId);
                reservation.BookingMode = newMode;
                reservation.DepartureTime = departure;
            }

            reservation.ChangeCount++;
            reservation.UpdatedAt = Now;
            await pmsContext.SaveChangesAsync();
            return true;
        });

        QueueReservationPush(reservation);
        await inventory.FlushAsync();
        return await ToModelAsync(reservation);
    }

    public async Task<ReservationResponseModel> CancelAsync(string userId, long id, string reason)
    {
        var reservation = await OwnAsync(userId, id, tracked: true);
        var cleanReason = string.IsNullOrWhiteSpace(reason) ? "Cancelled by the customer" : reason.Trim();

        if (reservation.Status == ReservationStatus.PendingDeposit)
        {
            // State machine: an unpaid booking the customer gives up on simply expires ("User bỏ")
            await EndAsync(reservation, ReservationStatus.Expired, cleanReason, "User", userId);
        }
        else if (reservation.Status == ReservationStatus.Confirmed)
        {
            var refund = RefundIfCancelledNow(reservation);
            await EndAsync(reservation, ReservationStatus.Cancelled, cleanReason, "User", userId);
            await paymentService.RefundDepositAsync(reservation, refund, $"Cancellation refund {reservation.ReservationCode}", userId);
        }
        else
        {
            throw new BadRequestException("This reservation can no longer be cancelled.");
        }

        await pmsContext.SaveChangesAsync();
        QueueReservationPush(reservation);
        await inventory.FlushAsync();
        return await ToModelAsync(reservation);
    }

    public async Task<ReservationQrModel> GetQrAsync(string userId, long id)
    {
        var reservation = await OwnAsync(userId, id, tracked: false);
        if (reservation.Status != ReservationStatus.Confirmed)
            throw new BadRequestException("The QR code is issued once the deposit is paid.");

        var validFrom = reservation.StartTime.AddMinutes(-Rules.EarlyArrivalMinutes);
        return new ReservationQrModel
        {
            ReservationId = reservation.Id,
            Code = reservation.ReservationCode,
            ValidFrom = validFrom,
            ValidUntil = reservation.EndTime,
            QrPayload = signer.Sign(ParkingTokenSigner.ReservationKind, reservation.ParkingLotId, new Dictionary<string, string>
            {
                ["id"] = reservation.Id.ToString(),
                ["code"] = reservation.ReservationCode,
                ["from"] = validFrom.ToString("O"),
                ["until"] = reservation.EndTime.ToString("O")
            })
        };
    }

    public async Task<ReservationResponseModel> CancelByLotAsync(long id, string reason)
    {
        var reservation = await pmsContext.Reservations.FirstOrDefaultAsync(r => r.Id == id)
                          ?? throw new NotFoundException("Reservation not found.");
        await lotScope.EnsureCanManageAsync(reservation.ParkingLotId);
        if (string.IsNullOrWhiteSpace(reason))
            throw new BadRequestException("A reason is required.");

        if (reservation.Status == ReservationStatus.PendingDeposit)
        {
            await EndAsync(reservation, ReservationStatus.Expired, reason.Trim(), "Lot", lotScope.UserId);
        }
        else if (reservation.Status == ReservationStatus.Confirmed)
        {
            await EndAsync(reservation, ReservationStatus.LotCancelled, reason.Trim(), "Lot", lotScope.UserId);
            // FR-RSV-09: the lot cancelled, so the whole deposit goes back
            var held = await paymentService.DepositHeldAsync(reservation.Id);
            await paymentService.RefundDepositAsync(reservation, held, $"Lot cancelled reservation {reservation.ReservationCode}", lotScope.UserId);
        }
        else
        {
            throw new BadRequestException("This reservation can no longer be cancelled.");
        }

        await pmsContext.SaveChangesAsync();
        QueueReservationPush(reservation);
        await inventory.FlushAsync();
        return await ToModelAsync(reservation);
    }

    public async Task<IReadOnlyList<ReservationResponseModel>> GetForLotAsync(int lotId, string status, DateTime? fromUtc, DateTime? toUtc)
    {
        await lotScope.EnsureCanOperateAsync(lotId);
        var query = pmsContext.Reservations.AsNoTracking().Where(r => r.ParkingLotId == lotId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
        if (fromUtc != null) query = query.Where(r => r.StartTime >= fromUtc);
        if (toUtc != null) query = query.Where(r => r.StartTime <= toUtc);
        return await ToModelsAsync(await query.OrderBy(r => r.StartTime).Take(500).ToListAsync());
    }

    /// <summary>
    /// Moves a reservation to a final state and frees its slot. Used by the user, the lot and the jobs
    /// (<see cref="Jobs.ParkingJobs"/>); the caller saves.
    /// </summary>
    internal async Task EndAsync(Reservation reservation, string status, string reason, string by, string userId)
    {
        ParkingStateMachines.EnsureReservation(reservation.Status, status);
        Log(reservation.Id, status, reservation.Status, reason, userId ?? by);
        reservation.Status = status;
        reservation.CancelReason = reason;
        reservation.CancelledBy = by;
        reservation.CancelledAt = Now;
        reservation.UpdatedAt = Now;
        // Only a booking that locked its bay gives it back; one booked further ahead never touched it
        if (reservation.SlotHeldAt != null)
            await inventory.ReleaseHoldAsync(reservation.SlotId, reservation.ParkingLotId);

        // Unpaid links of this booking stop being valid; a late success is refunded (FR-RSV-08)
        var open = await pmsContext.Payments
            .Where(p => p.ReservationId == reservation.Id && p.Status == PaymentStatus.Pending)
            .ToListAsync();
        foreach (var payment in open)
            payment.Status = PaymentStatus.Expired;
    }

    public async Task<GateReservationLookupModel> LookupForGateAsync(int lotId, string code, string qr, string plate = null)
    {
        await lotScope.EnsureCanOperateAsync(lotId);

        Reservation reservation;
        if (!string.IsNullOrWhiteSpace(qr))
        {
            var claims = signer.Verify(qr.Trim(), ParkingTokenSigner.ReservationKind);
            if (claims == null || !long.TryParse(claims.GetValueOrDefault("id"), out var id))
                throw new BadRequestException("This reservation QR is not genuine.");
            if (claims.GetValueOrDefault("lot") != lotId.ToString())
                throw new BadRequestException("This reservation is for another parking lot.");
            reservation = await pmsContext.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        }
        else if (!string.IsNullOrWhiteSpace(code))
        {
            var trimmed = code.Trim().ToUpperInvariant();
            reservation = await pmsContext.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.ReservationCode == trimmed);
            if (reservation != null && reservation.ParkingLotId != lotId)
                throw new BadRequestException("This reservation is for another parking lot.");
        }
        else if (!string.IsNullOrWhiteSpace(plate))
        {
            // The customer forgot the code: find the booking of this plate in this lot. A booking that can
            // still check in comes first, then the one closest to now, so the Staff sees why an old one cannot.
            var normalized = ParkingText.NormalizePlate(plate);
            if (normalized.Length == 0)
                throw new BadRequestException("Type a valid plate.");
            var candidates = await pmsContext.Reservations.AsNoTracking()
                .Where(r => r.ParkingLotId == lotId)
                .Join(pmsContext.Vehicles.Where(v => v.PlateNormalized == normalized), r => r.VehicleId, v => v.Id, (r, v) => r)
                .ToListAsync();
            reservation = candidates
                .OrderBy(r => r.Status == ReservationStatus.Confirmed ? 0 : 1)
                .ThenBy(r => Math.Abs((r.StartTime - Now).Ticks))
                .FirstOrDefault();
            if (reservation == null)
                throw new NotFoundException("No reservation found for this plate in this lot.");
        }
        else
        {
            throw new BadRequestException("Scan the reservation QR, type its code or type the plate.");
        }

        if (reservation == null)
            throw new NotFoundException("Reservation not found.");

        var vehicle = await pmsContext.Vehicles.AsNoTracking().Include(v => v.VehicleType).Include(v => v.Customer)
            .FirstOrDefaultAsync(v => v.Id == reservation.VehicleId);

        // The booked vehicle (or its plate) must not be parked anywhere already
        var openSession = await pmsContext.ParkingSessions.AsNoTracking()
            .Where(s => SessionStatus.Open.Contains(s.Status)
                        && (s.VehicleId == reservation.VehicleId
                            || (vehicle != null && s.HasPlate && s.EntryPlateNormalized == vehicle.PlateNormalized)))
            .Select(s => s.SessionCode)
            .FirstOrDefaultAsync();

        var from = reservation.StartTime.AddMinutes(-Rules.EarlyArrivalMinutes);
        string problem = null;
        if (reservation.Status != ReservationStatus.Confirmed)
            problem = reservation.Status == ReservationStatus.CheckedIn
                ? "This reservation has already checked in."
                : $"This reservation is {reservation.Status} and cannot check in.";
        else if (openSession != null)
            problem = $"This vehicle is already parked (session {openSession}).";
        else if (Now < from)
            problem = "Too early: check-in has not opened yet.";
        else if (Now > reservation.EndTime)
            problem = "The hold has ended; the vehicle can only enter as a walk-in.";

        return new GateReservationLookupModel
        {
            Reservation = await ToModelAsync(reservation),
            VehicleTypeId = vehicle?.VehicleTypeId ?? reservation.VehicleTypeId ?? 0,
            VehicleTypeName = vehicle?.VehicleType?.Name,
            VehicleBrand = vehicle?.Brand,
            VehicleModel = vehicle?.Model,
            VehicleColor = vehicle?.Color,
            CustomerName = vehicle?.Customer?.FullName,
            CanCheckIn = problem == null,
            Problem = problem,
            OpenSessionCode = openSession
        };
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> BookingGates = new();

    /// <summary>Runs one booking at a time per key (account, plate); keys are taken in a fixed order so two requests never deadlock.</summary>
    private static async Task<T> WithBookingLockAsync<T>(IEnumerable<string> keys, Func<Task<T>> action)
    {
        var gates = keys.Distinct().Order(StringComparer.Ordinal).Select(k => BookingGates.GetOrAdd(k, _ => new SemaphoreSlim(1, 1))).ToList();
        var taken = new List<SemaphoreSlim>();
        try
        {
            foreach (var gate in gates)
            {
                await gate.WaitAsync();
                taken.Add(gate);
            }
            return await action();
        }
        finally
        {
            foreach (var gate in taken)
                gate.Release();
        }
    }

    /// <summary>
    /// BR-07: at most N bookings in progress per account, and one per vehicle. The vehicle is matched by
    /// plate, so the same car added twice (or to two accounts) still gets only one booking.
    /// </summary>
    private async Task EnsureBookingLimitsAsync(int customerId, Vehicle vehicle)
    {
        var forAccount = await pmsContext.Reservations.AsNoTracking()
            .CountAsync(r => r.CustomerId == customerId && ReservationStatus.Active.Contains(r.Status));
        if (forAccount >= Rules.MaxActivePerAccount)
            throw new BadRequestException($"Each account can have at most {Rules.MaxActivePerAccount} active reservations.");

        var forPlate = await pmsContext.Reservations.AsNoTracking()
            .Where(r => ReservationStatus.Active.Contains(r.Status))
            .Join(pmsContext.Vehicles.Where(v => v.Id == vehicle.Id || v.PlateNormalized == vehicle.PlateNormalized),
                r => r.VehicleId, v => v.Id, (r, v) => r.Id)
            .CountAsync();
        if (forPlate >= Rules.MaxActivePerVehicle)
            throw new BadRequestException($"Vehicle {vehicle.PlateNumber} already has an active reservation.");
    }

    /// <summary>
    /// Checks the booking mode and times: arrival in the future and at most MaxLeadDays ahead; a
    /// Scheduled booking also needs a departure between MinStayMinutes and MaxStayHours after arrival.
    /// Returns the times in UTC to the minute (departure null for pay-as-you-go).
    /// </summary>
    private (DateTime Arrival, DateTime? Departure, string Mode) ValidateTimes(string mode, DateTimeOffset arrivalTime, DateTimeOffset? departureTime)
    {
        mode = BookingMode.All.FirstOrDefault(m => m.Equals(mode?.Trim() ?? BookingMode.PayAsYouGo, StringComparison.OrdinalIgnoreCase))
               ?? throw new BadRequestException("Booking mode must be Scheduled or PayAsYouGo.");

        var arrival = ToUtcMinute(arrivalTime);
        if (arrival <= Now)
            throw new BadRequestException("The arrival time must be in the future.");
        if (arrival > Now.AddDays(Rules.MaxLeadDays))
            throw new BadRequestException($"Arrival can be at most {Rules.MaxLeadDays} days ahead.");

        if (mode == BookingMode.PayAsYouGo)
            return (arrival, null, mode);

        if (departureTime == null)
            throw new BadRequestException("Pick a departure time for a scheduled booking, or book pay-as-you-go.");
        var departure = ToUtcMinute(departureTime.Value);
        if (departure < arrival.AddMinutes(Rules.MinStayMinutes))
            throw new BadRequestException($"The departure must be at least {Rules.MinStayMinutes} minutes after arrival.");
        if (departure > arrival.AddHours(Rules.MaxStayHours))
            throw new BadRequestException($"A scheduled booking can last at most {Rules.MaxStayHours} hours; book pay-as-you-go for longer stays.");
        return (arrival, departure, mode);
    }

    /// <summary>The bay is locked (Reserved) from HoldAheadMinutes before arrival; earlier it stays free for others.</summary>
    private bool ShouldHoldNow(DateTime arrival) => arrival.AddMinutes(-Rules.HoldAheadMinutes) <= Now;

    private decimal DepositFor(string vehicleTypeCode) =>
        Rules.DepositByVehicleType.TryGetValue(vehicleTypeCode, out var amount) ? amount : Rules.DefaultDeposit;

    /// <summary>BR-04: full refund up to H minutes before arrival, nothing after.</summary>
    private decimal RefundIfCancelledNow(Reservation reservation) =>
        reservation.Status == ReservationStatus.Confirmed && Now <= reservation.StartTime.AddMinutes(-Rules.CancelRefundMinutes)
            ? reservation.DepositAmount - reservation.RefundAmount
            : 0;

    /// <summary>
    /// The bay of the lot. When the booking locks it right away it must be free now; a booking for
    /// later only needs a bay that is in service (it may be occupied until then).
    /// </summary>
    private async Task<Slot> LoadSlotForLotAsync(int slotId, int lotId, bool mustBeFreeNow)
    {
        var slot = await pmsContext.Slots.Include(s => s.Zone).FirstOrDefaultAsync(s => s.Id == slotId)
                   ?? throw new NotFoundException("Slot not found.");
        if (slot.Zone?.ParkingLotId != lotId)
            throw new BadRequestException("The slot is not in this lot.");
        if (!slot.IsActive || slot.Status == SlotStatus.Maintenance)
            throw new ConflictException("This slot is out of service.");
        if (mustBeFreeNow && slot.Status != SlotStatus.Available)
            throw new ConflictException("This slot is no longer available.");
        return slot;
    }

    /// <summary>
    /// NFR-15 over time: no other booking of the bay overlaps the new window. A window runs from
    /// check-in opening (arrival - EarlyArrivalMinutes) to the departure; pay-as-you-go is open-ended.
    /// </summary>
    private async Task EnsureSlotFreeForAsync(int slotId, DateTime arrival, DateTime? departure, long? except)
    {
        var from = arrival.AddMinutes(-Rules.EarlyArrivalMinutes);
        var others = await pmsContext.Reservations.AsNoTracking()
            .Where(r => r.SlotId == slotId && r.Id != except && ReservationStatus.Active.Contains(r.Status))
            .Select(r => new { r.StartTime, r.DepartureTime, r.BookingMode })
            .ToListAsync();

        foreach (var other in others)
        {
            var otherFrom = other.StartTime.AddMinutes(-Rules.EarlyArrivalMinutes);
            DateTime? otherTo = other.BookingMode == BookingMode.Scheduled ? other.DepartureTime : null;
            var overlaps = (departure == null || otherFrom < departure) && (otherTo == null || from < otherTo);
            if (overlaps)
                throw new ConflictException("This bay is already booked for part of that time. Pick another time or bay.");
        }
    }

    /// <summary>
    /// Locks the bay of every booking that is now HoldAheadMinutes from arrival (run every minute).
    /// When the bay is taken (a car stayed longer, a walk-in parked there) the booking moves to another
    /// free bay of the same lot and vehicle type, same zone first. Returns how many bays were locked.
    /// </summary>
    public async Task<int> ActivateDueHoldsAsync()
    {
        var due = await pmsContext.Reservations
            .Where(r => (r.Status == ReservationStatus.PendingDeposit || r.Status == ReservationStatus.Confirmed)
                        && r.SlotHeldAt == null && r.SlotId != null && r.StartTime <= Now.AddMinutes(Rules.HoldAheadMinutes))
            .OrderBy(r => r.StartTime)
            .ToListAsync();
        if (due.Count == 0)
            return 0;

        var types = await inventory.VehicleTypesAsync();
        var held = 0;
        foreach (var reservation in due)
        {
            // The booked bay first, then free bays of the same lot that fit the vehicle
            var candidates = new List<int> { reservation.SlotId!.Value };
            candidates.AddRange(await pmsContext.Slots.AsNoTracking()
                .Where(s => s.Zone.ParkingLotId == reservation.ParkingLotId && s.IsActive && s.Status == SlotStatus.Available && s.Id != reservation.SlotId)
                .OrderBy(s => s.ZoneId == reservation.ZoneId ? 0 : 1).ThenBy(s => s.Id)
                .Select(s => s.Id)
                .Take(50)
                .ToListAsync());

            foreach (var candidateId in candidates)
            {
                bool locked;
                try
                {
                    locked = await TryHoldAsync(reservation, candidateId, types);
                }
                catch (ConflictException)
                {
                    // Someone else took the bay at the same moment: try the next one
                    locked = false;
                }
                if (!locked)
                    continue;
                held++;
                QueueReservationPush(reservation);
                break;
            }
        }

        await inventory.FlushAsync();
        return held;
    }

    /// <summary>Locks <paramref name="candidateId"/> for the booking when it is free and fits; moves the booking there if it is another bay.</summary>
    private Task<bool> TryHoldAsync(Reservation reservation, int candidateId, IReadOnlyDictionary<int, VehicleType> types) =>
        ParkingInventory.WithSlotLockAsync([candidateId], async () =>
        {
            var slot = await pmsContext.Slots.FirstOrDefaultAsync(s => s.Id == candidateId);
            if (slot is not { IsActive: true, Status: SlotStatus.Available }
                || !ParkingInventory.Compatible(types, slot.VehicleTypeId, reservation.VehicleTypeId ?? slot.VehicleTypeId))
                return false;
            if (candidateId != reservation.SlotId)
            {
                try
                {
                    await EnsureSlotFreeForAsync(candidateId, reservation.StartTime, reservation.DepartureTime, reservation.Id);
                }
                catch (ConflictException)
                {
                    return false;
                }
                Log(reservation.Id, "SlotChanged", reservation.SlotId?.ToString(), slot.Code, "System");
                reservation.SlotId = slot.Id;
                reservation.ZoneId = slot.ZoneId;
            }

            await inventory.HoldAsync(slot, reservation.ParkingLotId);
            reservation.SlotHeldAt = Now;
            reservation.UpdatedAt = Now;
            await pmsContext.SaveChangesAsync();
            return true;
        });

    /// <summary>The customer's next booking that has not started: the soonest arrival, or null.</summary>
    public async Task<ReservationResponseModel> GetUpcomingAsync(string userId)
    {
        var next = await pmsContext.Reservations.AsNoTracking()
            .Where(r => r.UserId == userId && (r.Status == ReservationStatus.PendingDeposit || r.Status == ReservationStatus.Confirmed))
            .OrderBy(r => r.StartTime)
            .FirstOrDefaultAsync();
        return next == null ? null : await ToModelAsync(next);
    }

    private async Task<Customer> CustomerAsync(string userId) =>
        await pmsContext.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == userId)
        ?? throw new BadRequestException("Add a vehicle to your profile first.");

    private async Task<Reservation> OwnAsync(string userId, long id, bool tracked)
    {
        var query = tracked ? pmsContext.Reservations : pmsContext.Reservations.AsNoTracking();
        return await query.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId)
               ?? throw new NotFoundException("Reservation not found.");
    }

    private void Log(long reservationId, string action, string oldValue, string newValue, string by) =>
        pmsContext.ReservationChangeLogs.Add(new ReservationChangeLog
        {
            ReservationId = reservationId,
            Action = action,
            OldValue = Truncate(oldValue),
            NewValue = Truncate(newValue),
            ChangedBy = by,
            ChangedAt = Now
        });

    private static string Truncate(string value) => value?.Length > 200 ? value[..200] : value;

    /// <summary>Arrival times are kept to the minute, in UTC.</summary>
    private static DateTime ToUtcMinute(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);
    }

    private void QueueReservationPush(Reservation r)
    {
        var (userId, id, lotId, status, slotId) = (r.UserId, r.Id, r.ParkingLotId, r.Status, r.SlotId);
        inventory.Queue(() => notifier?.ReservationChangedAsync(userId, id, lotId, status, slotId) ?? Task.CompletedTask);
    }

    private async Task<ReservationResponseModel> ToModelAsync(Reservation reservation) => (await ToModelsAsync([reservation]))[0];

    private async Task<List<ReservationResponseModel>> ToModelsAsync(IReadOnlyCollection<Reservation> reservations)
    {
        var lotIds = reservations.Select(r => r.ParkingLotId).Distinct().ToList();
        var slotIds = reservations.Where(r => r.SlotId != null).Select(r => r.SlotId.Value).Distinct().ToList();
        var vehicleIds = reservations.Select(r => r.VehicleId).Distinct().ToList();

        var lots = await pmsContext.ParkingLots.AsNoTracking().Where(p => lotIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Address, p.District }).ToDictionaryAsync(p => p.Id);
        var slots = await pmsContext.Slots.AsNoTracking().Where(s => slotIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Code, ZoneName = s.Zone.Name }).ToDictionaryAsync(s => s.Id);
        var vehicles = await pmsContext.Vehicles.AsNoTracking().Where(v => vehicleIds.Contains(v.Id))
            .Select(v => new { v.Id, v.PlateNumber, v.Brand, v.Model }).ToDictionaryAsync(v => v.Id);

        var models = new List<ReservationResponseModel>();
        foreach (var r in reservations)
        {
            var slot = r.SlotId != null ? slots.GetValueOrDefault(r.SlotId.Value) : null;
            var lot = lots.GetValueOrDefault(r.ParkingLotId);
            var vehicle = vehicles.GetValueOrDefault(r.VehicleId);
            var changeable = r.Status == ReservationStatus.Confirmed && Now <= r.StartTime.AddMinutes(-Rules.CancelRefundMinutes);
            models.Add(new ReservationResponseModel
            {
                Id = r.Id,
                Code = r.ReservationCode,
                Status = r.Status,
                ParkingLotId = r.ParkingLotId,
                ParkingLotName = lot?.Name,
                ParkingLotAddress = lot?.Address,
                ParkingLotDistrict = lot?.District,
                ZoneId = r.ZoneId,
                ZoneName = slot?.ZoneName,
                SlotId = r.SlotId,
                SlotCode = slot?.Code,
                VehicleId = r.VehicleId,
                PlateNumber = vehicle?.PlateNumber,
                VehicleLabel = vehicle == null ? null : string.Join(' ', new[] { vehicle.Brand, vehicle.Model }.Where(s => !string.IsNullOrWhiteSpace(s))),
                ArrivalTime = Utc(r.StartTime),
                BookingMode = r.BookingMode ?? BookingMode.PayAsYouGo,
                DepartureTime = r.DepartureTime is { } departure ? Utc(departure) : null,
                IsSlotHeld = r.SlotHeldAt != null && ReservationStatus.Active.Contains(r.Status),
                CheckInFrom = Utc(r.StartTime.AddMinutes(-Rules.EarlyArrivalMinutes)),
                CheckInUntil = Utc(r.EndTime),
                DepositAmount = r.DepositAmount,
                HasDeposit = r.DepositAmount > 0,
                HoldMinutes = (int)(r.EndTime - r.StartTime).TotalMinutes,
                DepositDueAt = r.DepositDueAt is { } due ? Utc(due) : null,
                RefundAmount = r.RefundAmount,
                CancelReason = r.CancelReason,
                CancelledBy = r.CancelledBy,
                CreatedAt = Utc(r.CreatedAt),
                ConfirmedAt = r.ConfirmedAt is { } c ? Utc(c) : null,
                CanChange = changeable,
                CanCancel = r.Status is ReservationStatus.PendingDeposit or ReservationStatus.Confirmed,
                RefundIfCancelledNow = RefundIfCancelledNow(r),
                PendingPayment = r.Status == ReservationStatus.PendingDeposit ? await paymentService.GetPendingForReservationAsync(r.Id) : null
            });
        }

        return models;
    }

    /// <summary>SQL Server hands datetimes back unspecified; they are UTC.</summary>
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
