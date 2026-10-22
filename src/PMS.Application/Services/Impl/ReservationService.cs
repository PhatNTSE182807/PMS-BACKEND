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
        var arrival = ToUtcMinute(model.ArrivalTime);
        EnsureArrivalAllowed(arrival);

        var lot = await pmsContext.ParkingLots.AsNoTracking().FirstOrDefaultAsync(p => p.Id == model.ParkingLotId && !p.IsDeleted)
                  ?? throw new NotFoundException("Parking lot not found.");
        if (lot.Status != "Active")
            throw new BadRequestException("The lot is not taking reservations right now.");

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

            var slot = await LoadSlotForLotAsync(model.SlotId, lot.Id);
            if (!ParkingInventory.Compatible(types, slot.VehicleTypeId, vehicle.VehicleTypeId))
                throw new BadRequestException("This slot is for another vehicle type.");
            await EnsureSlotHasNoActiveReservationAsync(slot.Id, null);

            await inventory.HoldAsync(slot, lot.Id);
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
                EndTime = arrival.AddMinutes(Rules.HoldMinutes),
                DepositAmount = DepositFor(vehicleType.Code),
                DepositDueAt = Now.AddMinutes(Rules.DepositMinutes),
                Status = ReservationStatus.PendingDeposit,
                CreatedAt = Now
            };
            pmsContext.Reservations.Add(created);
            await pmsContext.SaveChangesAsync();

            Log(created.Id, "Created", null, $"{slot.Code} @ {arrival:O}", userId);
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

        if (model.ArrivalTime is { } newArrivalOffset)
        {
            var newArrival = ToUtcMinute(newArrivalOffset);
            EnsureArrivalAllowed(newArrival);
            if (newArrival != reservation.StartTime)
            {
                Log(reservation.Id, "ArrivalChanged", reservation.StartTime.ToString("O"), newArrival.ToString("O"), userId);
                reservation.StartTime = newArrival;
                reservation.EndTime = newArrival.AddMinutes(Rules.HoldMinutes);
                reservation.ReminderSentAt = null;
                reservation.HoldReminderSentAt = null;
            }
        }

        if (model.SlotId is { } newSlotId && newSlotId != reservation.SlotId)
        {
            var types = await inventory.VehicleTypesAsync();
            var oldSlotId = reservation.SlotId;
            await ParkingInventory.WithSlotLockAsync(new[] { newSlotId, oldSlotId ?? newSlotId }, async () =>
            {
                var slot = await LoadSlotForLotAsync(newSlotId, reservation.ParkingLotId);
                if (!ParkingInventory.Compatible(types, slot.VehicleTypeId, reservation.VehicleTypeId ?? slot.VehicleTypeId))
                    throw new BadRequestException("This slot is for another vehicle type.");
                await EnsureSlotHasNoActiveReservationAsync(slot.Id, reservation.Id);

                await inventory.HoldAsync(slot, reservation.ParkingLotId);
                await inventory.ReleaseHoldAsync(oldSlotId, reservation.ParkingLotId);
                Log(reservation.Id, "SlotChanged", oldSlotId?.ToString(), slot.Code, userId);
                reservation.SlotId = slot.Id;
                reservation.ZoneId = slot.ZoneId;
                reservation.ChangeCount++;
                reservation.UpdatedAt = Now;
                await pmsContext.SaveChangesAsync();
                return true;
            });
        }
        else
        {
            reservation.ChangeCount++;
            reservation.UpdatedAt = Now;
            await pmsContext.SaveChangesAsync();
        }

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
        await inventory.ReleaseHoldAsync(reservation.SlotId, reservation.ParkingLotId);

        // Unpaid links of this booking stop being valid; a late success is refunded (FR-RSV-08)
        var open = await pmsContext.Payments
            .Where(p => p.ReservationId == reservation.Id && p.Status == PaymentStatus.Pending)
            .ToListAsync();
        foreach (var payment in open)
            payment.Status = PaymentStatus.Expired;
    }

    public async Task<GateReservationLookupModel> LookupForGateAsync(int lotId, string code, string qr)
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
        else
        {
            throw new BadRequestException("Scan the reservation QR or type its code.");
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

    private void EnsureArrivalAllowed(DateTime arrival)
    {
        if (arrival <= Now)
            throw new BadRequestException("The arrival time must be in the future.");
        if (arrival > Now.AddHours(Rules.MaxLeadHours))
            throw new BadRequestException($"Arrival must be within {Rules.MaxLeadHours} hours (BR-01).");
    }

    private decimal DepositFor(string vehicleTypeCode) =>
        Rules.DepositByVehicleType.TryGetValue(vehicleTypeCode, out var amount) ? amount : Rules.DefaultDeposit;

    /// <summary>BR-04: full refund up to H minutes before arrival, nothing after.</summary>
    private decimal RefundIfCancelledNow(Reservation reservation) =>
        reservation.Status == ReservationStatus.Confirmed && Now <= reservation.StartTime.AddMinutes(-Rules.CancelRefundMinutes)
            ? reservation.DepositAmount - reservation.RefundAmount
            : 0;

    private async Task<Slot> LoadSlotForLotAsync(int slotId, int lotId)
    {
        var slot = await pmsContext.Slots.Include(s => s.Zone).FirstOrDefaultAsync(s => s.Id == slotId)
                   ?? throw new NotFoundException("Slot not found.");
        if (slot.Zone?.ParkingLotId != lotId)
            throw new BadRequestException("The slot is not in this lot.");
        if (!slot.IsActive || slot.Status != SlotStatus.Available)
            throw new ConflictException("This slot is no longer available.");
        return slot;
    }

    private async Task EnsureSlotHasNoActiveReservationAsync(int slotId, long? except)
    {
        if (await pmsContext.Reservations.AnyAsync(r => r.SlotId == slotId && r.Id != except
                                                        && (r.Status == ReservationStatus.PendingDeposit || r.Status == ReservationStatus.Confirmed || r.Status == ReservationStatus.CheckedIn)))
            throw new ConflictException("This slot was just taken by someone else.");
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

        var lots = await pmsContext.ParkingLots.AsNoTracking().Where(p => lotIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name);
        var slots = await pmsContext.Slots.AsNoTracking().Where(s => slotIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Code, ZoneName = s.Zone.Name }).ToDictionaryAsync(s => s.Id);
        var plates = await pmsContext.Vehicles.AsNoTracking().Where(v => vehicleIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.PlateNumber);

        var models = new List<ReservationResponseModel>();
        foreach (var r in reservations)
        {
            var slot = r.SlotId != null ? slots.GetValueOrDefault(r.SlotId.Value) : null;
            var changeable = r.Status == ReservationStatus.Confirmed && Now <= r.StartTime.AddMinutes(-Rules.CancelRefundMinutes);
            models.Add(new ReservationResponseModel
            {
                Id = r.Id,
                Code = r.ReservationCode,
                Status = r.Status,
                ParkingLotId = r.ParkingLotId,
                ParkingLotName = lots.GetValueOrDefault(r.ParkingLotId),
                ZoneId = r.ZoneId,
                ZoneName = slot?.ZoneName,
                SlotId = r.SlotId,
                SlotCode = slot?.Code,
                VehicleId = r.VehicleId,
                PlateNumber = plates.GetValueOrDefault(r.VehicleId),
                ArrivalTime = Utc(r.StartTime),
                CheckInFrom = Utc(r.StartTime.AddMinutes(-Rules.EarlyArrivalMinutes)),
                CheckInUntil = Utc(r.EndTime),
                DepositAmount = r.DepositAmount,
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
