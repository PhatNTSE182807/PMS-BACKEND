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
public class GateService(
    PmsDbContext pmsContext,
    ParkingOptions options,
    ParkingInventory inventory,
    TariffProvider tariffs,
    IPaymentService paymentService,
    ParkingTokenSigner signer,
    LotScopeService lotScope,
    TimeProvider clock,
    IParkingRealtimeNotifier notifier = null,
    IUserNotificationPublisher notifications = null) : IGateService
{
    /// <summary>Two lanes never open two sessions for one plate at the same time (NFR-14) inside this process.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PlateGates = new();

    private GateRules Rules => options.Gate;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ---- lookups ----------------------------------------------------------------------------------

    public async Task<IReadOnlyList<GateResponseModel>> GetGatesAsync(int lotId)
    {
        await lotScope.EnsureCanOperateAsync(lotId);
        var gates = await EnsureGatesAsync(lotId);
        return gates.Select(g => new GateResponseModel { Id = g.Id, Code = g.Code, Name = g.Name, Direction = g.Direction }).ToList();
    }

    public async Task<IReadOnlyList<ZoneCounterModel>> GetZoneCountersAsync(int lotId)
    {
        await lotScope.EnsureCanOperateAsync(lotId);
        return await inventory.CountersAsync(lotId);
    }

    public async Task<IReadOnlyList<SessionResponseModel>> GetSessionsAsync(int lotId, string status, string plate)
    {
        await lotScope.EnsureCanOperateAsync(lotId);
        var query = pmsContext.ParkingSessions.AsNoTracking().Where(s => s.ParkingLotId == lotId);
        query = string.IsNullOrWhiteSpace(status)
            ? query.Where(s => SessionStatus.Open.Contains(s.Status))
            : query.Where(s => s.Status == status);
        var normalized = ParkingText.NormalizePlate(plate);
        if (normalized.Length > 0)
            query = query.Where(s => s.EntryPlateNormalized.Contains(normalized));

        return await ToModelsAsync(await query.OrderByDescending(s => s.EntryTime).Take(300).ToListAsync());
    }

    public async Task<SessionResponseModel> GetSessionAsync(long sessionId) =>
        await ToModelAsync(await ScopedSessionAsync(sessionId));

    public async Task<SessionResponseModel> LookupAsync(int lotId, string plate, string ticket, string code)
    {
        await lotScope.EnsureCanOperateAsync(lotId);
        var query = pmsContext.ParkingSessions.AsNoTracking().Where(s => s.ParkingLotId == lotId && SessionStatus.Open.Contains(s.Status));

        if (!string.IsNullOrWhiteSpace(ticket))
        {
            // Signed ticket from the entry (works even when the QR was printed while the gate was offline),
            // or the exit pass the customer shows in the app
            var claims = signer.Verify(ticket, ParkingTokenSigner.TicketKind)
                         ?? signer.Verify(ticket, ParkingTokenSigner.ExitPassKind)
                         ?? throw new BadRequestException("The ticket QR is not genuine.");
            if (claims.GetValueOrDefault("lot") != lotId.ToString())
                throw new BadRequestException("This ticket is for another parking lot.");
            code = claims.GetValueOrDefault("sc");
        }

        ParkingSession session;
        if (!string.IsNullOrWhiteSpace(code))
            session = await query.FirstOrDefaultAsync(s => s.SessionCode == code.Trim());
        else
        {
            var normalized = ParkingText.NormalizePlate(plate);
            if (normalized.Length == 0)
                throw new BadRequestException("Give a plate, a ticket or a session code.");
            session = await query.Where(s => s.EntryPlateNormalized == normalized).OrderByDescending(s => s.EntryTime).FirstOrDefaultAsync();
        }

        return session == null
            ? throw new NotFoundException("No open session found.")
            : await ToModelAsync(session);
    }

    // ---- entry ------------------------------------------------------------------------------------

    public async Task<CheckInResultModel> CheckInAsync(int lotId, CheckInModel model)
    {
        await lotScope.EnsureCanOperateAsync(lotId);
        var lot = await pmsContext.ParkingLots.AsNoTracking().FirstOrDefaultAsync(p => p.Id == lotId && !p.IsDeleted)
                  ?? throw new NotFoundException("Parking lot not found.");
        if (model.IsManual && string.IsNullOrWhiteSpace(model.ManualReason))
            throw new BadRequestException("A manual entry needs a reason.");

        var gate = await ResolveGateAsync(lotId, model.GateId, "In");
        var alerts = new List<GateAlertModel>();
        var overrides = new List<string>();
        var plate = model.NoPlate ? string.Empty : model.PlateNumber?.Trim().ToUpperInvariant() ?? string.Empty;
        var normalized = ParkingText.NormalizePlate(plate);
        var confidence = NormalizeConfidence(model.AnprConfidence);

        if (lot.Status != "Active")
            alerts.Add(Block("LOT_CLOSED", "The lot is closed for entry.", canOverride: false));

        if (!model.NoPlate && normalized.Length == 0)
            alerts.Add(Block("PLATE_REQUIRED", "Enter the plate or mark the vehicle as plate-less.", false));

        // FR-OPS-14: low ANPR confidence → the Staff confirms or corrects the plate first
        if (!model.NoPlate && confidence < Rules.AnprConfidenceThreshold)
            alerts.Add(Overridable("LOW_CONFIDENCE", $"ANPR is only {confidence:P0} confident; check the plate.", model.PlateConfirmed, overrides, "plate confirmed"));

        // Reservation: signed QR, typed code, or the plate of a verified vehicle (FR-RSV-04, BR-02)
        Reservation reservation = null;
        if (!model.IgnoreReservation)
            reservation = await FindReservationAsync(lotId, model, normalized, alerts, overrides);

        var types = await inventory.VehicleTypesAsync();
        var laneType = model.VehicleTypeId ?? reservation?.VehicleTypeId
                       ?? (model.NoPlate ? types.Values.FirstOrDefault(t => t.Code == "BICYCLE")?.Id : null);
        if (laneType == null)
        {
            // A refused reservation QR already blocks the lane; report that instead of the missing type
            if (alerts.Any(a => a.Blocking))
                return new CheckInResultModel { Outcome = "Blocked", BarrierOpen = false, Alerts = alerts };
            throw new BadRequestException("The lane's vehicle type is required.");
        }
        var vehicleTypeId = laneType.Value;
        if (!types.ContainsKey(vehicleTypeId))
            throw new BadRequestException("Unknown vehicle type.");

        // FR-OPS-11: one vehicle is parked in one place. The plate, or the booked vehicle when a reservation
        // is used with another plate, must not have an open session in any lot.
        var bookedVehicleId = reservation?.VehicleId;
        var bookedPlate = bookedVehicleId != null
            ? await pmsContext.Vehicles.AsNoTracking().Where(v => v.Id == bookedVehicleId).Select(v => v.PlateNormalized).FirstOrDefaultAsync() ?? string.Empty
            : string.Empty;
        if (normalized.Length > 0 || bookedVehicleId != null)
        {
            var open = await pmsContext.ParkingSessions.AsNoTracking()
                .Where(s => SessionStatus.Open.Contains(s.Status)
                            && ((normalized.Length > 0 && s.HasPlate && s.EntryPlateNormalized == normalized)
                                || (bookedPlate.Length > 0 && s.HasPlate && s.EntryPlateNormalized == bookedPlate)
                                || (bookedVehicleId != null && s.VehicleId == bookedVehicleId)))
                .Select(s => new { s.Id, s.SessionCode })
                .FirstOrDefaultAsync();
            if (open != null)
                alerts.Add(new GateAlertModel
                {
                    Code = "DUPLICATE_SESSION",
                    Message = $"This vehicle is already parked (open session {open.SessionCode}): close the ghost session or refuse entry (possible fake plate).",
                    Blocking = true,
                    CanOverride = false,
                    RelatedSessionId = open.Id
                });
        }

        // BR-16 / BR-17
        var whitelisted = false;
        if (normalized.Length > 0)
        {
            var lists = await pmsContext.PlateLists.AsNoTracking()
                .Where(p => p.IsActive && p.PlateNormalized == normalized && p.ExpiresAt > Now && (p.ParkingLotId == lotId || p.ParkingLotId == null))
                .ToListAsync();
            var black = lists.FirstOrDefault(p => p.ListType == PlateListType.Blacklist);
            if (black != null)
                alerts.Add(Overridable("BLACKLISTED", $"Blacklisted plate: {black.Reason}.", model.AllowBlacklisted, overrides, "blacklist allowed"));
            var white = lists.FirstOrDefault(p => p.ListType == PlateListType.Whitelist);
            if (white != null)
            {
                whitelisted = true;
                alerts.Add(new GateAlertModel { Code = "WHITELISTED", Message = $"Whitelisted: {white.Reason} (free of charge).", Blocking = false });
            }
        }

        // Zone Counter (FR-OPS-15): only walk-ins are counted against free spaces; a reservation has its slot
        int? zoneId = reservation?.ZoneId;
        int? slotId = reservation?.SlotId;
        if (reservation == null)
        {
            var counters = (await inventory.CountersAsync(lotId))
                .Where(c => c.VehicleTypeId != null && ParkingInventory.Compatible(types, c.VehicleTypeId.Value, vehicleTypeId))
                .ToList();

            if (model.SlotId is { } chosen)
            {
                var slot = await pmsContext.Slots.AsNoTracking().Include(s => s.Zone).FirstOrDefaultAsync(s => s.Id == chosen);
                if (slot == null || slot.Zone?.ParkingLotId != lotId || slot.Status != SlotStatus.Available || !slot.IsActive
                    || !ParkingInventory.Compatible(types, slot.VehicleTypeId, vehicleTypeId))
                    alerts.Add(Block("SLOT_UNAVAILABLE", "The chosen bay is not free or is for another vehicle type. Pick another bay.", false));
                else
                {
                    slotId = slot.Id;
                    zoneId = slot.ZoneId;
                }
            }
            else if (counters.Count > 0)
            {
                zoneId = counters.OrderByDescending(c => c.AvailableSpaces).First().ZoneId;
            }

            var target = counters.FirstOrDefault(c => c.ZoneId == zoneId);
            if (target is { IsFull: true } && !whitelisted)
                alerts.Add(Overridable("ZONE_FULL", $"{target.ZoneName} is full.", model.AllowWhenFull, overrides, "zone full allowed"));
        }

        if (overrides.Count > 0 && string.IsNullOrWhiteSpace(model.OverrideReason))
            throw new BadRequestException("Give a reason for the override.");

        if (alerts.Any(a => a.Blocking))
        {
            var codes = string.Join(", ", alerts.Where(a => a.Blocking).Select(a => a.Code));
            inventory.Queue(() => notifier?.OpsAlertAsync(lotId, "ENTRY_BLOCKED", $"{gate.Code}: {(plate.Length > 0 ? plate : "plate-less vehicle")} — {codes}", null) ?? Task.CompletedTask);
            await inventory.FlushAsync();
            return new CheckInResultModel { Outcome = "Blocked", BarrierOpen = false, Alerts = alerts };
        }

        // A verified vehicle entering without a booking is linked to its owner's account (section 4.1, User)
        var (customerId, vehicleId) = reservation != null
            ? ((int?)reservation.CustomerId, (Guid?)reservation.VehicleId)
            : await VerifiedOwnerAsync(normalized);

        // Lock on the vehicle when it is known (a reservation may come with another plate), else on the plate
        var session = await WithPlateLockAsync(vehicleId != null ? $"vehicle:{vehicleId}" : normalized, async () =>
        {
            if (await pmsContext.ParkingSessions.AnyAsync(s => SessionStatus.Open.Contains(s.Status)
                    && ((normalized.Length > 0 && s.HasPlate && s.EntryPlateNormalized == normalized)
                        || (bookedPlate.Length > 0 && s.HasPlate && s.EntryPlateNormalized == bookedPlate)
                        || (vehicleId != null && s.VehicleId == vehicleId))))
                throw new ConflictException("This vehicle just entered at another lane.");
            // The same reservation can be used for one entry only (two lanes scanning one QR)
            if (reservation != null && await pmsContext.Reservations.AnyAsync(r => r.Id == reservation.Id && r.Status != ReservationStatus.Confirmed))
                throw new ConflictException("This reservation has already checked in.");

            var tariff = await tariffs.ForAsync(lotId, reservation?.ZoneId, vehicleTypeId, Now);
            var created = new ParkingSession
            {
                SessionCode = ParkingText.NewCode("SS", Now),
                ParkingLotId = lotId,
                CustomerId = customerId,
                VehicleId = vehicleId,
                ReservationId = reservation?.Id,
                EntryGateId = gate.Id,
                EntryTime = TrimToSecond(Now),
                EntryPlateNumber = plate,
                EntryPlateNormalized = normalized,
                EntryImageOverviewUrl = model.ImageUrl,
                EntryOperatorUserId = lotScope.UserId,
                EntryAnprConfidence = model.AnprConfidence == null ? null : decimal.Round(confidence * 100, 2),
                ZoneId = zoneId,
                SlotId = slotId,
                PricingRuleId = tariff.RuleId,
                PricingRuleVersion = tariff.Version,
                Status = SessionStatus.PendingEntry,
                SessionType = reservation != null ? SessionType.Reservation : whitelisted ? SessionType.Whitelist : SessionType.WalkIn,
                VehicleTypeId = vehicleTypeId,
                HasPlate = !model.NoPlate,
                IsManualEntry = model.IsManual,
                ManualEntryReason = model.IsManual ? model.ManualReason.Trim() : null,
                OverrideNote = overrides.Count > 0 ? $"{string.Join("; ", overrides)}: {model.OverrideReason.Trim()}" : null,
                CreatedAt = Now,
                RowVersion = []
            };
            pmsContext.ParkingSessions.Add(created);
            await pmsContext.SaveChangesAsync();
            return created;
        });

        QueueSessionPush(session);
        await inventory.FlushAsync();

        return new CheckInResultModel
        {
            Outcome = "Created",
            BarrierOpen = true,
            Alerts = alerts,
            Session = await ToModelAsync(session),
            Ticket = signer.Sign(ParkingTokenSigner.TicketKind, lotId, new Dictionary<string, string>
            {
                ["sc"] = session.SessionCode,
                ["lotCode"] = lot.Code,
                ["in"] = session.EntryTime.ToString("O"),
                ["lane"] = gate.Code
            })
        };
    }

    public async Task<SessionResponseModel> ConfirmEntryAsync(long sessionId)
    {
        var session = await ScopedSessionAsync(sessionId, tracked: true);
        ParkingStateMachines.EnsureSession(session.Status, SessionStatus.Active);
        session.Status = SessionStatus.Active;
        session.EntryConfirmedAt = Now;
        session.UpdatedAt = Now;

        await inventory.ParkAsync(session.ZoneId, session.SlotId, session.ParkingLotId);

        if (session.ReservationId != null)
        {
            var reservation = await pmsContext.Reservations.FirstOrDefaultAsync(r => r.Id == session.ReservationId);
            if (reservation is { Status: ReservationStatus.Confirmed })
            {
                ParkingStateMachines.EnsureReservation(reservation.Status, ReservationStatus.CheckedIn);
                reservation.Status = ReservationStatus.CheckedIn;
                reservation.CheckedInAt = Now;
                reservation.UpdatedAt = Now;
                pmsContext.ReservationChangeLogs.Add(new ReservationChangeLog
                {
                    ReservationId = reservation.Id, Action = "CheckedIn", OldValue = ReservationStatus.Confirmed,
                    NewValue = session.SessionCode, ChangedBy = lotScope.UserId, ChangedAt = Now
                });
                var (userId, id, lotId, slotId) = (reservation.UserId, reservation.Id, reservation.ParkingLotId, reservation.SlotId);
                inventory.Queue(() => notifier?.ReservationChangedAsync(userId, id, lotId, ReservationStatus.CheckedIn, slotId) ?? Task.CompletedTask);
            }
        }

        await pmsContext.SaveChangesAsync();
        QueueSessionPush(session);
        await inventory.FlushAsync();
        // Bell notification with the check-in time for a registered customer's vehicle
        if (session.CustomerId != null)
            notifications?.SessionCheckedIn(session.Id);
        return await ToModelAsync(session);
    }

    public async Task<SessionResponseModel> CancelEntryAsync(long sessionId)
    {
        var session = await ScopedSessionAsync(sessionId, tracked: true);
        await CancelPendingEntryAsync(session, "Vehicle turned around / cancelled by Staff");
        await pmsContext.SaveChangesAsync();
        await inventory.FlushAsync();
        return await ToModelAsync(session);
    }

    /// <summary>Shared with the turn-around job: no sensor signal, so nothing was counted (4.2.1).</summary>
    internal Task CancelPendingEntryAsync(ParkingSession session, string note)
    {
        ParkingStateMachines.EnsureSession(session.Status, SessionStatus.Cancelled);
        session.Status = SessionStatus.Cancelled;
        session.ClosedNote = note;
        session.UpdatedAt = Now;
        QueueSessionPush(session);
        return Task.CompletedTask;
    }

    // ---- payment points ---------------------------------------------------------------------------

    public async Task<CheckoutQuoteModel> RequestPaymentAsync(long sessionId, CheckoutQuoteRequestModel model)
    {
        var session = await ScopedSessionAsync(sessionId, tracked: true);
        var quote = await QuoteAsync(session, model?.PlateNumber, model?.AnprConfidence, mutate: true);
        await pmsContext.SaveChangesAsync();
        QueueSessionPush(session);
        await inventory.FlushAsync();
        quote.Session = await ToModelAsync(session);
        return quote;
    }

    public async Task<SessionResponseModel> CancelPaymentRequestAsync(long sessionId)
    {
        var session = await ScopedSessionAsync(sessionId, tracked: true);
        ParkingStateMachines.EnsureSession(session.Status, SessionStatus.Active);
        if (session.Status != SessionStatus.PendingPayment)
            throw new BadRequestException("The session is not waiting for payment.");
        session.Status = SessionStatus.Active;
        session.UpdatedAt = Now;
        await pmsContext.SaveChangesAsync();
        QueueSessionPush(session);
        await inventory.FlushAsync();
        return await ToModelAsync(session);
    }

    public async Task<PaymentResponseModel> PayCashAsync(long sessionId, CashPaymentModel model)
    {
        var session = await ScopedSessionAsync(sessionId, tracked: true);
        var quote = await QuoteAsync(session, null, null, mutate: true);
        await pmsContext.SaveChangesAsync();
        if (quote.AmountDue <= 0)
            throw new BadRequestException("Nothing is due.");

        return await paymentService.RecordCashAsync(session, quote.AmountDue, model.Amount, model.Prepaid, lotScope.UserId);
    }

    public async Task<PaymentResponseModel> PayOnlineAtLaneAsync(long sessionId, LanePaymentModel model, PaymentRequestContext context)
    {
        var session = await ScopedSessionAsync(sessionId, tracked: true);
        var quote = await QuoteAsync(session, null, null, mutate: true);
        await pmsContext.SaveChangesAsync();
        if (quote.AmountDue <= 0)
            throw new BadRequestException("Nothing is due.");

        var customerUserId = session.CustomerId == null
            ? null
            : await pmsContext.Customers.Where(c => c.Id == session.CustomerId).Select(c => c.UserId).FirstOrDefaultAsync();
        return await paymentService.CreateSessionPaymentAsync(session, quote.AmountDue, model.Provider,
            model.Prepaid ? PaymentChannel.Kiosk : PaymentChannel.Lane, null, customerUserId, lotScope.UserId, context);
    }

    // ---- exit -------------------------------------------------------------------------------------

    public async Task<CheckoutResultModel> CheckoutAsync(long sessionId, CheckoutModel model)
    {
        var session = await ScopedSessionAsync(sessionId, tracked: true);
        if (model.IsManual && string.IsNullOrWhiteSpace(model.ManualReason))
            throw new BadRequestException("A manual exit needs a reason.");

        var quote = await QuoteAsync(session, model.PlateNumber, null, mutate: true);
        await pmsContext.SaveChangesAsync();
        if (session.Status != SessionStatus.Paid)
        {
            QueueSessionPush(session);
            await inventory.FlushAsync();
            throw new BadRequestException(session.ExitWindowEndsAt != null && Now > session.ExitWindowEndsAt
                ? $"The Exit Window has passed: {quote.AmountDue:N0} VND more is due (BR-10)."
                : $"{quote.AmountDue:N0} VND is still due.");
        }

        // FR-OPS-04 / FR-OPS-10: plates or photos must match unless the Staff lets it pass with a reason
        var overrides = new List<string>();
        var alerts = new List<GateAlertModel>();
        foreach (var alert in quote.Alerts.Where(a => a.Blocking))
        {
            var cleared = alert.Code switch
            {
                "PLATE_MISMATCH" => model.AllowPlateMismatch,
                "PHOTO_CHECK_REQUIRED" or "LOW_CONFIDENCE" => model.PhotoChecked,
                _ => false
            };
            if (cleared) overrides.Add(alert.Code);
            else alerts.Add(alert);
        }

        if (alerts.Count > 0)
            return new CheckoutResultModel { Session = await ToModelAsync(session), BarrierOpen = false, Alerts = alerts };
        if (overrides.Contains("PLATE_MISMATCH") && string.IsNullOrWhiteSpace(model.OverrideReason))
            throw new BadRequestException("A reason is required to let a vehicle out when the plates differ.");

        var gate = await ResolveGateAsync(session.ParkingLotId, model.GateId, "Out");
        ParkingStateMachines.EnsureSession(session.Status, SessionStatus.Completed);
        session.Status = SessionStatus.Completed;
        session.ExitTime = TrimToSecond(Now);
        session.ExitGateId = gate.Id;
        session.ExitOperatorUserId = lotScope.UserId;
        session.ExitPlateNumber = model.PlateNumber?.Trim().ToUpperInvariant();
        session.ExitPlateNormalized = ParkingText.NormalizePlate(model.PlateNumber);
        session.IsManualExit = model.IsManual;
        session.ManualExitReason = model.IsManual ? model.ManualReason.Trim() : null;
        if (overrides.Count > 0)
            session.OverrideNote = string.Join(" | ", new[] { session.OverrideNote, $"exit: {string.Join(", ", overrides)} {model.OverrideReason}".Trim() }.Where(n => !string.IsNullOrWhiteSpace(n)));
        session.UpdatedAt = Now;

        await inventory.LeaveAsync(session.ZoneId, session.SlotId, session.ParkingLotId);

        decimal refunded = 0;
        if (session.ReservationId != null)
        {
            var reservation = await pmsContext.Reservations.FirstOrDefaultAsync(r => r.Id == session.ReservationId);
            if (reservation is { Status: ReservationStatus.CheckedIn })
            {
                reservation.Status = ReservationStatus.Completed;
                reservation.CompletedAt = Now;
                reservation.UpdatedAt = Now;
                // BR-03: what the fee did not use of the deposit goes back automatically
                refunded = await paymentService.RefundDepositAsync(reservation, quote.DepositRefund, $"Deposit remainder refund {reservation.ReservationCode}", "System");
                var (userId, id, lotId, slotId) = (reservation.UserId, reservation.Id, reservation.ParkingLotId, reservation.SlotId);
                inventory.Queue(() => notifier?.ReservationChangedAsync(userId, id, lotId, ReservationStatus.Completed, slotId) ?? Task.CompletedTask);
            }
        }

        await pmsContext.SaveChangesAsync();
        QueueSessionPush(session);
        await inventory.FlushAsync();
        // Bell notification with the check-out time, duration and fee
        if (session.CustomerId != null)
            notifications?.SessionCheckedOut(session.Id);
        return new CheckoutResultModel { Session = await ToModelAsync(session), BarrierOpen = true, DepositRefund = refunded };
    }

    public async Task<SessionResponseModel> ReportLostTicketAsync(long sessionId, LostTicketModel model)
    {
        var session = await ScopedSessionAsync(sessionId, tracked: true);
        if (string.IsNullOrWhiteSpace(model?.Reason))
            throw new BadRequestException("A reason is required.");
        ParkingStateMachines.EnsureSession(session.Status, SessionStatus.LostTicketPending);

        var tariff = await tariffs.ForAsync(session.ParkingLotId, session.ReservationId != null ? session.ZoneId : null, session.VehicleTypeId, session.EntryTime);
        session.IsLostCard = true;
        session.Status = SessionStatus.LostTicketPending;
        session.UpdatedAt = Now;
        pmsContext.SessionViolations.Add(new SessionViolation
        {
            SessionId = session.Id,
            ViolationType = "LostTicket",
            FineAmount = tariff.LostTicketFee,
            Note = model.Reason.Trim(),
            ReportedBy = lotScope.UserId,
            ReportedAt = Now
        });

        if (model.ConditionsMet)
        {
            // BR-13: conditional approval; the Manager reviews it within 24 h
            session.Status = SessionStatus.PendingPayment;
            await QuoteAsync(session, null, null, mutate: true);
        }
        else
        {
            var (lotId, id, code) = (session.ParkingLotId, session.Id, session.SessionCode);
            inventory.Queue(() => notifier?.OpsAlertAsync(lotId, "LOST_TICKET_APPROVAL", $"Lost ticket {code} needs a Manager's approval.", id) ?? Task.CompletedTask);
        }

        await pmsContext.SaveChangesAsync();
        QueueSessionPush(session);
        await inventory.FlushAsync();
        return await ToModelAsync(session);
    }

    public async Task<SessionResponseModel> DecideLostTicketAsync(long sessionId, bool approve, string reason)
    {
        var session = await pmsContext.ParkingSessions.FirstOrDefaultAsync(s => s.Id == sessionId)
                      ?? throw new NotFoundException("Session not found.");
        await lotScope.EnsureCanManageAsync(session.ParkingLotId);
        if (session.Status != SessionStatus.LostTicketPending)
            throw new BadRequestException("No lost-ticket case is waiting.");

        if (approve)
        {
            session.Status = SessionStatus.PendingPayment;
            await QuoteAsync(session, null, null, mutate: true);
        }
        else
        {
            // 4.2.4: the vehicle stays; the customer comes back with full papers
            session.Status = SessionStatus.Active;
        }

        session.ClosedNote = $"Lost ticket {(approve ? "approved" : "rejected")}: {reason}".Trim();
        session.UpdatedAt = Now;
        await pmsContext.SaveChangesAsync();
        QueueSessionPush(session);
        await inventory.FlushAsync();
        return await ToModelAsync(session);
    }

    public async Task<SessionResponseModel> CloseAbnormallyAsync(long sessionId, string reason)
    {
        var session = await pmsContext.ParkingSessions.FirstOrDefaultAsync(s => s.Id == sessionId)
                      ?? throw new NotFoundException("Session not found.");
        await lotScope.EnsureCanManageAsync(session.ParkingLotId);
        if (string.IsNullOrWhiteSpace(reason))
            throw new BadRequestException("A reason is required.");
        if (session.Status is SessionStatus.PendingPayment or SessionStatus.LostTicketPending)
            session.Status = SessionStatus.Active;
        ParkingStateMachines.EnsureSession(session.Status, SessionStatus.AbnormalClosed);

        session.Status = SessionStatus.AbnormalClosed;
        session.ClosedNote = reason.Trim();
        session.ExitTime = TrimToSecond(Now);
        session.ExitOperatorUserId = lotScope.UserId;
        session.UpdatedAt = Now;
        await inventory.LeaveAsync(session.ZoneId, session.SlotId, session.ParkingLotId);

        if (session.ReservationId != null)
        {
            var reservation = await pmsContext.Reservations.FirstOrDefaultAsync(r => r.Id == session.ReservationId);
            if (reservation is { Status: ReservationStatus.CheckedIn })
            {
                reservation.Status = ReservationStatus.Completed;
                reservation.CompletedAt = Now;
            }
        }

        await pmsContext.SaveChangesAsync();
        QueueSessionPush(session);
        await inventory.FlushAsync();
        if (session.CustomerId != null)
            notifications?.SessionCheckedOut(session.Id);
        return await ToModelAsync(session);
    }

    // ---- customer ---------------------------------------------------------------------------------

    public async Task<IReadOnlyList<SessionResponseModel>> GetMySessionsAsync(string userId)
    {
        var customerId = await CustomerIdAsync(userId);
        var sessions = await pmsContext.ParkingSessions.AsNoTracking()
            .Where(s => s.CustomerId == customerId && s.Status != SessionStatus.Cancelled)
            .OrderByDescending(s => s.EntryTime)
            .Take(50)
            .ToListAsync();
        return (await ToModelsAsync(sessions)).OrderByDescending(s => SessionStatus.Inside.Contains(s.Status)).ToList();
    }

    public async Task<CheckoutQuoteModel> QuoteMySessionAsync(string userId, long sessionId)
    {
        var session = await OwnSessionAsync(userId, sessionId, tracked: false);
        var quote = await QuoteAsync(session, null, null, mutate: false);
        quote.Session = await ToModelAsync(session);
        return quote;
    }

    public async Task<PaymentResponseModel> PayMySessionAsync(string userId, long sessionId, CreatePaymentModel model, PaymentRequestContext context)
    {
        var session = await OwnSessionAsync(userId, sessionId, tracked: true);
        var quote = await QuoteAsync(session, null, null, mutate: true);
        await pmsContext.SaveChangesAsync();
        if (quote.AmountDue <= 0)
            throw new BadRequestException("Nothing is due.");

        return await paymentService.CreateSessionPaymentAsync(session, quote.AmountDue, model.Provider, PaymentChannel.App,
            model.ReturnUrl, userId, null, context);
    }

    public async Task<IReadOnlyList<CurrentSessionModel>> GetMyCurrentSessionsAsync(string userId)
    {
        // No customer profile yet means nothing parked
        var customerId = await pmsContext.Customers.Where(c => c.UserId == userId).Select(c => (int?)c.Id).FirstOrDefaultAsync();
        if (customerId == null)
            return [];

        // Only confirmed entries: a vehicle still at the barrier (PendingEntry) is not parked yet
        var sessions = await pmsContext.ParkingSessions.AsNoTracking()
            .Where(s => s.CustomerId == customerId && SessionStatus.Inside.Contains(s.Status))
            .OrderByDescending(s => s.EntryTime)
            .ToListAsync();
        if (sessions.Count == 0)
            return [];

        var lotIds = sessions.Select(s => s.ParkingLotId).Distinct().ToList();
        var gateIds = sessions.Select(s => s.EntryGateId).Distinct().ToList();
        var vehicleIds = sessions.Where(s => s.VehicleId != null).Select(s => s.VehicleId!.Value).Distinct().ToList();
        var reservationIds = sessions.Where(s => s.ReservationId != null).Select(s => s.ReservationId!.Value).Distinct().ToList();
        var lots = await pmsContext.ParkingLots.AsNoTracking().Where(l => lotIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id);
        var gateNames = await pmsContext.Gates.AsNoTracking().Where(g => gateIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name);
        var vehicles = await pmsContext.Vehicles.AsNoTracking().Where(v => vehicleIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id);
        var bookings = await pmsContext.Reservations.AsNoTracking().Where(r => reservationIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id);
        var models = await ToModelsAsync(sessions);

        var result = new List<CurrentSessionModel>();
        foreach (var (session, model) in sessions.Zip(models))
        {
            // A read-only quote: looking at the fee never changes the session
            var quote = await QuoteAsync(session, null, null, mutate: false);
            var tariff = await tariffs.ForAsync(session.ParkingLotId, session.ReservationId != null ? session.ZoneId : null, session.VehicleTypeId, session.EntryTime);
            var lot = lots.GetValueOrDefault(session.ParkingLotId);
            var vehicle = session.VehicleId != null ? vehicles.GetValueOrDefault(session.VehicleId.Value) : null;
            var booking = session.ReservationId != null ? bookings.GetValueOrDefault(session.ReservationId.Value) : null;

            result.Add(new CurrentSessionModel
            {
                Session = model,
                LotName = lot?.Name,
                LotAddress = lot?.Address,
                LotDistrict = lot?.District,
                EntryGateName = gateNames.GetValueOrDefault(session.EntryGateId),
                EntryMethod = session.ReservationId != null ? "Reservation" : session.IsManualEntry ? "Manual" : "Plate",
                VehicleLabel = vehicle == null ? null : string.Join(' ', new[] { vehicle.Brand, vehicle.Model }.Where(s => !string.IsNullOrWhiteSpace(s))),
                BookingMode = booking?.BookingMode,
                PlannedDeparture = booking?.DepartureTime is { } departure ? Utc(departure) : null,
                ElapsedMinutes = Math.Max(0, (int)Math.Floor((Now - session.EntryTime).TotalMinutes)),
                FeeSoFar = quote.Fee + quote.LostTicketFee + quote.ExitWindowLateFee,
                DepositApplied = quote.DepositApplied,
                AlreadyPaid = quote.AlreadyPaid,
                AmountDue = quote.AmountDue,
                Tariff = new CurrentTariffModel
                {
                    GraceMinutes = tariff.GraceMinutes,
                    BlockMinutes = tariff.BlockMinutes,
                    BlockPrice = tariff.BlockPrice,
                    MaxDailyFee = tariff.MaxDailyFee
                },
                PendingPayment = await paymentService.GetPendingForSessionAsync(session.Id),
                ExitPass = ExitPassFor(session, quote.AmountDue)
            });
        }

        return result;
    }

    public async Task<ExitPassModel> GetMyExitPassAsync(string userId, long sessionId)
    {
        var session = await OwnSessionAsync(userId, sessionId, tracked: false);
        if (!SessionStatus.Inside.Contains(session.Status))
            throw new BadRequestException("The exit pass is available while the vehicle is inside the lot.");
        var quote = await QuoteAsync(session, null, null, mutate: false);
        return ExitPassFor(session, quote.AmountDue);
    }

    /// <summary>
    /// Every parked vehicle has an exit pass from check-in on. It carries the session code (the exit lane
    /// finds the session from it and charges what is due); after a prepayment it also carries the end of
    /// the Exit Window, so an offline gate can open the barrier on it alone (FR-PAY-07).
    /// </summary>
    private ExitPassModel ExitPassFor(ParkingSession session, decimal amountDue)
    {
        var prepaid = session.Status == SessionStatus.Paid && session.ExitWindowEndsAt is { } until && until > Now;
        var claims = new Dictionary<string, string>
        {
            ["sc"] = session.SessionCode,
            ["in"] = Utc(session.EntryTime).ToString("O")
        };
        if (prepaid)
            claims["until"] = Utc(session.ExitWindowEndsAt!.Value).ToString("O");

        return new ExitPassModel
        {
            SessionId = session.Id,
            SessionCode = session.SessionCode,
            Token = signer.Sign(ParkingTokenSigner.ExitPassKind, session.ParkingLotId, claims),
            Prepaid = prepaid,
            ValidUntil = prepaid ? Utc(session.ExitWindowEndsAt!.Value) : null,
            AmountDue = prepaid ? 0 : amountDue
        };
    }

    // ---- fee --------------------------------------------------------------------------------------

    /// <summary>
    /// Prices the stay up to now (BR-10, BR-22, FR-RSV-07). With <paramref name="mutate"/> it is a fee request
    /// at a payment point: Active → PendingPayment, Paid past its Exit Window → PendingPayment, nothing due → Paid.
    /// </summary>
    private async Task<CheckoutQuoteModel> QuoteAsync(ParkingSession session, string exitPlate, decimal? exitConfidence, bool mutate)
    {
        if (session.Status == SessionStatus.LostTicketPending)
            throw new BadRequestException("The lost-ticket case is waiting for a Manager.");
        if (!SessionStatus.Inside.Contains(session.Status))
            throw new BadRequestException($"The session is {session.Status} and cannot be charged.");

        var lot = await pmsContext.ParkingLots.AsNoTracking().FirstAsync(p => p.Id == session.ParkingLotId);
        var timeZone = ParkingText.FindTimeZone(lot.TimeZoneId ?? options.TimeZoneId);
        var tariff = await tariffs.ForAsync(session.ParkingLotId, session.ReservationId != null ? session.ZoneId : null, session.VehicleTypeId, session.EntryTime);

        var breakdown = session.SessionType == SessionType.Whitelist
            ? new FeeBreakdown((int)Math.Ceiling((Now - session.EntryTime).TotalMinutes), 0, false, [])
            : FeeCalculator.Calculate(tariff, Utc(session.EntryTime), Now, timeZone);
        var deposit = session.ReservationId != null ? await paymentService.DepositHeldAsync(session.ReservationId.Value) : 0;
        var exitWindowMissed = session.ExitWindowEndsAt != null && Now > session.ExitWindowEndsAt && session.PaidAmount > 0;
        var lostFee = session.IsLostCard ? tariff.LostTicketFee : 0;

        // Paid at the lane (no Exit Window), or prepaid and still inside the window: the payment stands
        var settledPaid = session.Status == SessionStatus.Paid && !exitWindowMissed;
        var settlement = settledPaid
            ? new Settlement(session.OriginalFee, 0, 0, session.DepositApplied, session.PaidAmount, 0, Math.Max(0, deposit - session.DepositApplied))
            : FeeCalculator.Settle(breakdown.Total, deposit, session.PaidAmount, exitWindowMissed, Rules.ExitWindowLateFee, lostFee);

        var zeroReason = settledPaid ? session.ZeroFeeReason
            : session.SessionType == SessionType.Whitelist ? "Whitelist"
            : breakdown.WithinGrace && lostFee == 0 ? "GracePeriod"
            : settlement.AmountDue == 0 && session.PaidAmount == 0 && deposit > 0 ? "CoveredByDeposit"
            : null;

        if (mutate && !settledPaid)
        {
            // Fee request at a payment point; a missed Exit Window sends a paid session back (BR-10)
            if (session.Status is SessionStatus.Active or SessionStatus.Paid)
                session.Status = SessionStatus.PendingPayment;

            // "Đã trả hoặc phí = 0đ" → Paid; BR-09: 0 VND creates no payment, only the reason
            if (session.Status == SessionStatus.PendingPayment && settlement.AmountDue == 0)
            {
                session.Status = SessionStatus.Paid;
                session.ExitWindowEndsAt = null;
            }

            session.TotalDurationMinutes = breakdown.DurationMinutes;
            session.OriginalFee = breakdown.Total;
            session.FinalFee = settlement.Fee + settlement.LostTicketFee + settlement.ExitWindowLateFee;
            session.DepositApplied = settlement.DepositApplied;
            session.ZeroFeeReason = zeroReason;
            session.PricingRuleId ??= tariff.RuleId;
            session.PricingRuleVersion ??= tariff.Version;
            session.UpdatedAt = Now;
        }

        // FR-OPS-04 / FR-OPS-10 / FR-OPS-14 at the exit
        var alerts = new List<GateAlertModel>();
        var exitNormalized = ParkingText.NormalizePlate(exitPlate);
        if (!session.HasPlate)
            alerts.Add(Block("PHOTO_CHECK_REQUIRED", "Compare the entry and exit photos.", true));
        else if (exitNormalized.Length > 0 && exitNormalized != session.EntryPlateNormalized)
        {
            if (mutate) session.PlateMismatch = true;
            alerts.Add(Block("PLATE_MISMATCH", $"Exit plate {exitPlate} differs from entry plate {session.EntryPlateNumber}.", true));
        }
        if (session.HasPlate && exitConfidence != null && NormalizeConfidence(exitConfidence) < Rules.AnprConfidenceThreshold)
            alerts.Add(Block("LOW_CONFIDENCE", "Exit ANPR confidence is low: compare the entry and exit photos.", true));

        return new CheckoutQuoteModel
        {
            DurationMinutes = breakdown.DurationMinutes,
            Fee = settlement.Fee,
            LostTicketFee = settlement.LostTicketFee,
            ExitWindowLateFee = settlement.ExitWindowLateFee,
            DepositApplied = settlement.DepositApplied,
            AlreadyPaid = settlement.AlreadyPaid,
            AmountDue = settlement.AmountDue,
            DepositRefund = settlement.DepositRefund,
            ZeroFeeReason = zeroReason,
            CanOpenBarrier = settlement.AmountDue == 0 && !exitWindowMissed && alerts.All(a => !a.Blocking),
            Alerts = alerts,
            Days = breakdown.Days.Select(d => new FeeDayModel { Date = d.Date, Minutes = d.Minutes, Blocks = d.Blocks, Amount = d.Amount, Capped = d.Capped }).ToList(),
            PricingRuleId = tariff.RuleId,
            PricingRuleVersion = tariff.Version
        };
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private async Task<Reservation> FindReservationAsync(int lotId, CheckInModel model, string normalized, List<GateAlertModel> alerts, List<string> overrides)
    {
        Reservation reservation = null;
        var explicitMatch = false;

        if (!string.IsNullOrWhiteSpace(model.ReservationQr))
        {
            var claims = signer.Verify(model.ReservationQr, ParkingTokenSigner.ReservationKind);
            if (claims == null || claims.GetValueOrDefault("lot") != lotId.ToString() || !long.TryParse(claims.GetValueOrDefault("id"), out var id))
            {
                alerts.Add(Block("RESERVATION_INVALID", "Invalid reservation QR (enter as walk-in instead).", true));
                return null;
            }
            reservation = await pmsContext.Reservations.FirstOrDefaultAsync(r => r.Id == id);
            explicitMatch = true;
        }
        else if (!string.IsNullOrWhiteSpace(model.ReservationCode))
        {
            reservation = await pmsContext.Reservations.FirstOrDefaultAsync(r => r.ParkingLotId == lotId && r.ReservationCode == model.ReservationCode.Trim());
            explicitMatch = true;
        }
        else if (normalized.Length > 0)
        {
            // Plate check-in only for vehicles whose papers were verified (FR-RSV-04, section 4.2.6)
            var candidates = await pmsContext.Reservations
                .Where(r => r.ParkingLotId == lotId && r.Status == ReservationStatus.Confirmed)
                .Join(pmsContext.Vehicles.Where(v => v.PlateNormalized == normalized), r => r.VehicleId, v => v.Id, (r, v) => r)
                .ToListAsync();
            foreach (var candidate in candidates.OrderBy(r => Math.Abs((r.StartTime - Now).Ticks)))
            {
                if (await IsVerifiedAsync(candidate.VehicleId))
                {
                    reservation = candidate;
                    break;
                }
            }
        }

        if (reservation == null)
        {
            if (explicitMatch)
                alerts.Add(Block("RESERVATION_INVALID", "Reservation not found (enter as walk-in instead).", true));
            return null;
        }

        if (reservation.ParkingLotId != lotId || reservation.Status != ReservationStatus.Confirmed)
        {
            alerts.Add(Block("RESERVATION_INVALID", $"Reservation {reservation.ReservationCode} is {reservation.Status} and cannot check in. Enter as walk-in.", true));
            return null;
        }

        // BR-02: check-in window
        var from = reservation.StartTime.AddMinutes(-options.Reservations.EarlyArrivalMinutes);
        if (Now < from || Now > reservation.EndTime)
        {
            alerts.Add(Block("RESERVATION_OUTSIDE_WINDOW",
                Now < from
                    ? $"Too early: reservation check-in opens at {from:HH:mm} UTC. Wait, or enter as walk-in (the booking is kept)."
                    : "The hold has ended; enter as walk-in.", true));
            return null;
        }

        // QR / code and plate must match (4.2.1); a valid change of vehicle is the Staff's call
        if (explicitMatch && normalized.Length > 0)
        {
            var bookedPlate = await pmsContext.Vehicles.Where(v => v.Id == reservation.VehicleId).Select(v => v.PlateNormalized).FirstOrDefaultAsync();
            if (bookedPlate != normalized)
                alerts.Add(Overridable("RESERVATION_PLATE_MISMATCH", "The plate differs from the booking; verify it is a vehicle of the same account and type.",
                    model.AllowPlateMismatch, overrides, "reservation vehicle changed"));
        }

        return reservation;
    }

    private async Task<bool> IsVerifiedAsync(Guid vehicleId)
    {
        var latest = await pmsContext.VehicleVerifications.AsNoTracking()
            .Where(v => v.VehicleId == vehicleId)
            .OrderByDescending(v => v.CreatedAt)
            .Select(v => v.Status)
            .FirstOrDefaultAsync();
        return latest is VehicleVerificationStatus.Approved or VehicleVerificationStatus.AutoVerified;
    }

    private async Task<(int?, Guid?)> VerifiedOwnerAsync(string normalized)
    {
        if (normalized.Length == 0) return (null, null);
        var vehicles = await pmsContext.Vehicles.AsNoTracking()
            .Where(v => v.PlateNormalized == normalized && v.IsActive && v.CustomerId != null)
            .ToListAsync();
        foreach (var vehicle in vehicles)
            if (await IsVerifiedAsync(vehicle.Id))
                return (vehicle.CustomerId, vehicle.Id);
        return (null, null);
    }

    /// <summary>Lots built from a template have no lanes yet: one entry and one exit lane are added.</summary>
    private async Task<List<Gate>> EnsureGatesAsync(int lotId)
    {
        var gates = await pmsContext.Gates.Where(g => g.ParkingLotId == lotId && g.IsActive).OrderBy(g => g.Code).ToListAsync();
        if (gates.Count > 0) return gates;

        gates =
        [
            new Gate { ParkingLotId = lotId, Code = "IN-1", Name = "Entry lane 1", Direction = "In", IsActive = true },
            new Gate { ParkingLotId = lotId, Code = "OUT-1", Name = "Exit lane 1", Direction = "Out", IsActive = true }
        ];
        pmsContext.Gates.AddRange(gates);
        await pmsContext.SaveChangesAsync();
        return gates;
    }

    private async Task<Gate> ResolveGateAsync(int lotId, int? gateId, string direction)
    {
        var gates = await EnsureGatesAsync(lotId);
        if (gateId != null)
            return gates.FirstOrDefault(g => g.Id == gateId)
                   ?? throw new BadRequestException("The lane is not in this lot.");
        return gates.FirstOrDefault(g => g.Direction == direction) ?? gates.FirstOrDefault(g => g.Direction == "Both") ?? gates[0];
    }

    private async Task<ParkingSession> ScopedSessionAsync(long sessionId, bool tracked = false)
    {
        var query = tracked ? pmsContext.ParkingSessions : pmsContext.ParkingSessions.AsNoTracking();
        var session = await query.FirstOrDefaultAsync(s => s.Id == sessionId)
                      ?? throw new NotFoundException("Session not found.");
        await lotScope.EnsureCanOperateAsync(session.ParkingLotId);
        return session;
    }

    private async Task<int> CustomerIdAsync(string userId) =>
        await pmsContext.Customers.Where(c => c.UserId == userId).Select(c => (int?)c.Id).FirstOrDefaultAsync()
        ?? throw new NotFoundException("Customer profile not found.");

    private async Task<ParkingSession> OwnSessionAsync(string userId, long sessionId, bool tracked)
    {
        var customerId = await CustomerIdAsync(userId);
        var query = tracked ? pmsContext.ParkingSessions : pmsContext.ParkingSessions.AsNoTracking();
        return await query.FirstOrDefaultAsync(s => s.Id == sessionId && s.CustomerId == customerId)
               ?? throw new NotFoundException("Session not found.");
    }

    private static async Task<T> WithPlateLockAsync<T>(string plate, Func<Task<T>> action)
    {
        if (plate.Length == 0) return await action();
        var gate = PlateGates.GetOrAdd(plate, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            return await action();
        }
        catch (DbUpdateException e) when (e.InnerException?.Message.Contains("IX_ParkingSessions_OpenPlate") == true)
        {
            throw new ConflictException("This plate just entered at another lane.");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>ANPR confidence as 0..1 (accepts percentages); typed plates count as certain.</summary>
    private static decimal NormalizeConfidence(decimal? value) =>
        value == null ? 1m : value > 1 ? value.Value / 100m : value.Value;

    private static GateAlertModel Block(string code, string message, bool canOverride) =>
        new() { Code = code, Message = message, Blocking = true, CanOverride = canOverride };

    /// <summary>A blocking alert the Staff already cleared becomes informational and is written to the override note.</summary>
    private static GateAlertModel Overridable(string code, string message, bool cleared, List<string> overrides, string note)
    {
        if (cleared) overrides.Add(note);
        return new GateAlertModel { Code = code, Message = message, Blocking = !cleared, CanOverride = true };
    }

    private static DateTime TrimToSecond(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private void QueueSessionPush(ParkingSession s)
    {
        var (lotId, id, status, plate) = (s.ParkingLotId, s.Id, s.Status, s.EntryPlateNumber);
        inventory.Queue(() => notifier?.SessionChangedAsync(lotId, id, status, plate) ?? Task.CompletedTask);
    }

    private async Task<SessionResponseModel> ToModelAsync(ParkingSession session) => (await ToModelsAsync([session]))[0];

    private async Task<List<SessionResponseModel>> ToModelsAsync(IReadOnlyCollection<ParkingSession> sessions)
    {
        var typeNames = await pmsContext.VehicleTypes.AsNoTracking().ToDictionaryAsync(v => v.Id, v => v.Name);
        var zoneIds = sessions.Where(s => s.ZoneId != null).Select(s => s.ZoneId.Value).Distinct().ToList();
        var slotIds = sessions.Where(s => s.SlotId != null).Select(s => s.SlotId.Value).Distinct().ToList();
        var reservationIds = sessions.Where(s => s.ReservationId != null).Select(s => s.ReservationId.Value).Distinct().ToList();
        var zones = await pmsContext.Zones.AsNoTracking().Where(z => zoneIds.Contains(z.Id)).ToDictionaryAsync(z => z.Id, z => z.Name);
        var slots = await pmsContext.Slots.AsNoTracking().Where(s => slotIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Code);
        var codes = await pmsContext.Reservations.AsNoTracking().Where(r => reservationIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.ReservationCode);
        var overdueBefore = Now.AddHours(-Rules.OverdueHours);

        return sessions.Select(s => new SessionResponseModel
        {
            Id = s.Id,
            Code = s.SessionCode,
            ParkingLotId = s.ParkingLotId,
            Status = s.Status,
            SessionType = s.SessionType,
            PlateNumber = s.EntryPlateNumber,
            HasPlate = s.HasPlate,
            VehicleTypeId = s.VehicleTypeId,
            VehicleTypeName = s.VehicleTypeId != null ? typeNames.GetValueOrDefault(s.VehicleTypeId.Value) : null,
            EntryTime = Utc(s.EntryTime),
            EntryConfirmedAt = s.EntryConfirmedAt is { } c ? Utc(c) : null,
            EntryGateId = s.EntryGateId,
            ZoneId = s.ZoneId,
            ZoneName = s.ZoneId != null ? zones.GetValueOrDefault(s.ZoneId.Value) : null,
            SlotId = s.SlotId,
            SlotCode = s.SlotId != null ? slots.GetValueOrDefault(s.SlotId.Value) : null,
            ReservationId = s.ReservationId,
            ReservationCode = s.ReservationId != null ? codes.GetValueOrDefault(s.ReservationId.Value) : null,
            IsCustomerAccount = s.CustomerId != null,
            PaidAmount = s.PaidAmount,
            FinalFee = s.FinalFee,
            ExitWindowEndsAt = s.ExitWindowEndsAt is { } w ? Utc(w) : null,
            ExitTime = s.ExitTime is { } x ? Utc(x) : null,
            IsManualEntry = s.IsManualEntry,
            IsLostTicket = s.IsLostCard,
            PlateMismatch = s.PlateMismatch,
            IsOverdue = SessionStatus.Inside.Contains(s.Status) && s.EntryTime < overdueBefore,
            ZeroFeeReason = s.ZeroFeeReason,
            OverrideNote = s.OverrideNote
        }).ToList();
    }
}
