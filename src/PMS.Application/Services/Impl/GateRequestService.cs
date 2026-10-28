using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <inheritdoc />
public class GateRequestService(
    PmsDbContext pmsContext,
    IGateService gateService,
    LotScopeService lotScope,
    ParkingOptions options,
    TimeProvider clock,
    IParkingRealtimeNotifier notifier = null) : IGateRequestService
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private int EarlyArrivalMinutes => options.Reservations.EarlyArrivalMinutes;

    /* ------------------------------------------------------------------ customer */

    public async Task<GateRequestModel> RequestCheckInAsync(string userId, long reservationId)
    {
        var reservation = await pmsContext.Reservations.AsNoTracking()
                              .FirstOrDefaultAsync(r => r.Id == reservationId && r.UserId == userId)
                          ?? throw new NotFoundException("Reservation not found.");

        // The same rules the entry lane applies, so the customer learns at once when it cannot work
        if (reservation.Status != ReservationStatus.Confirmed)
            throw new BadRequestException(reservation.Status == ReservationStatus.CheckedIn
                ? "This booking has already checked in."
                : $"This booking is {reservation.Status} and cannot check in.");
        if (Now < reservation.StartTime.AddMinutes(-EarlyArrivalMinutes))
            throw new BadRequestException($"Too early: check-in opens {EarlyArrivalMinutes} minutes before your arrival time.");
        if (Now > reservation.EndTime)
            throw new BadRequestException("The hold has ended; the vehicle can only enter as a walk-in at the lane.");

        var parked = await pmsContext.ParkingSessions.AsNoTracking()
            .AnyAsync(s => s.VehicleId == reservation.VehicleId && SessionStatus.Open.Contains(s.Status));
        if (parked)
            throw new BadRequestException("This vehicle is already parked.");

        // Pressing twice keeps one place in the queue
        var waiting = await pmsContext.GateRequests.AsNoTracking()
            .FirstOrDefaultAsync(g => g.ReservationId == reservationId && g.Kind == GateRequestKind.CheckIn && g.Status == GateRequestStatus.Waiting);
        if (waiting != null)
            return (await ToModelsAsync([waiting]))[0];

        var vehicle = await pmsContext.Vehicles.AsNoTracking().FirstOrDefaultAsync(v => v.Id == reservation.VehicleId);
        var request = new GateRequest
        {
            ParkingLotId = reservation.ParkingLotId,
            Kind = GateRequestKind.CheckIn,
            Status = GateRequestStatus.Waiting,
            UserId = userId,
            CustomerId = vehicle?.CustomerId,
            ReservationId = reservation.Id,
            VehicleId = reservation.VehicleId,
            PlateNumber = vehicle?.PlateNumber,
            CreatedAt = Now
        };
        pmsContext.GateRequests.Add(request);
        await pmsContext.SaveChangesAsync();
        await PushAsync(request);
        return (await ToModelsAsync([request]))[0];
    }

    public async Task<GateRequestModel> RequestCheckOutAsync(string userId, long sessionId)
    {
        var customerId = await CustomerIdAsync(userId);
        var session = await pmsContext.ParkingSessions.AsNoTracking()
                          .FirstOrDefaultAsync(s => s.Id == sessionId && s.CustomerId == customerId)
                      ?? throw new NotFoundException("Parking session not found.");
        if (!SessionStatus.Inside.Contains(session.Status))
            throw new BadRequestException("This vehicle is not parked.");

        var waiting = await pmsContext.GateRequests.AsNoTracking()
            .FirstOrDefaultAsync(g => g.SessionId == sessionId && g.Kind == GateRequestKind.CheckOut && g.Status == GateRequestStatus.Waiting);
        if (waiting != null)
            return (await ToModelsAsync([waiting]))[0];

        var request = new GateRequest
        {
            ParkingLotId = session.ParkingLotId,
            Kind = GateRequestKind.CheckOut,
            Status = GateRequestStatus.Waiting,
            UserId = userId,
            CustomerId = customerId,
            ReservationId = session.ReservationId,
            SessionId = session.Id,
            VehicleId = session.VehicleId,
            PlateNumber = session.EntryPlateNumber,
            CreatedAt = Now
        };
        pmsContext.GateRequests.Add(request);
        await pmsContext.SaveChangesAsync();
        await PushAsync(request);
        return (await ToModelsAsync([request]))[0];
    }

    public async Task<GateRequestModel> CancelAsync(string userId, long requestId)
    {
        var request = await pmsContext.GateRequests.FirstOrDefaultAsync(g => g.Id == requestId && g.UserId == userId)
                      ?? throw new NotFoundException("Request not found.");
        if (request.Status != GateRequestStatus.Waiting)
            throw new BadRequestException("Staff already handled this request.");

        Close(request, GateRequestStatus.Cancelled, userId, null);
        await pmsContext.SaveChangesAsync();
        await PushAsync(request);
        return (await ToModelsAsync([request]))[0];
    }

    public async Task<IReadOnlyList<GateRequestModel>> GetMineAsync(string userId)
    {
        var since = Now.AddHours(-12);
        var requests = await pmsContext.GateRequests.AsNoTracking()
            .Where(g => g.UserId == userId && g.CreatedAt >= since)
            .OrderByDescending(g => g.CreatedAt)
            .Take(20)
            .ToListAsync();
        return await ToModelsAsync(requests);
    }

    /* ------------------------------------------------------------------ Staff */

    public async Task<IReadOnlyList<GateRequestModel>> GetQueueAsync(int lotId, string kind)
    {
        await lotScope.EnsureCanOperateAsync(lotId);
        var kinds = string.IsNullOrWhiteSpace(kind)
            ? GateRequestKind.All
            : [GateRequestKind.All.FirstOrDefault(k => k.Equals(kind.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new BadRequestException("Kind must be CheckIn or CheckOut.")];

        var requests = await pmsContext.GateRequests
            .Where(g => g.ParkingLotId == lotId && kinds.Contains(g.Kind) && g.Status == GateRequestStatus.Waiting)
            .OrderBy(g => g.CreatedAt)
            .ToListAsync();

        // A booking checked in (or a vehicle let out) at the lane without the request no longer needs it
        var stale = await StaleAsync(requests);
        foreach (var request in stale)
            Close(request, GateRequestStatus.Cancelled, lotScope.UserId, "Handled at the lane without the app request.");
        if (stale.Count > 0)
        {
            await pmsContext.SaveChangesAsync();
            foreach (var request in stale)
                await PushAsync(request);
        }

        return await ToModelsAsync(requests.Except(stale).ToList());
    }

    public async Task<GateRequestDecisionModel> ApproveAsync(long requestId, ApproveGateRequestModel model)
    {
        model ??= new ApproveGateRequestModel();
        var request = await WaitingForStaffAsync(requestId);
        var decision = new GateRequestDecisionModel();

        if (request.Kind == GateRequestKind.CheckIn)
        {
            var reservation = await pmsContext.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.ReservationId)
                              ?? throw new NotFoundException("Reservation not found.");
            var vehicleTypeId = await pmsContext.Vehicles.AsNoTracking()
                .Where(v => v.Id == reservation.VehicleId).Select(v => (int?)v.VehicleTypeId).FirstOrDefaultAsync();

            // Every entry rule still runs (window, zone full, blacklist...); a block keeps the request waiting
            var result = await gateService.CheckInAsync(request.ParkingLotId, new CheckInModel
            {
                GateId = model.GateId,
                PlateNumber = request.PlateNumber,
                VehicleTypeId = vehicleTypeId ?? reservation.VehicleTypeId,
                PlateConfirmed = model.PlateConfirmed,
                ReservationCode = reservation.ReservationCode,
                AllowWhenFull = model.AllowWhenFull,
                AllowPlateMismatch = model.AllowPlateMismatch,
                OverrideReason = model.OverrideReason
            });
            decision.CheckIn = result;
            decision.BarrierOpen = result.BarrierOpen;
            decision.Alerts = result.Alerts.Where(a => a.Blocking).ToList();
            if (result.Session != null && result.Outcome != "Blocked")
                request.SessionId = result.Session.Id;
        }
        else
        {
            // The fee must be paid (or 0 VND); otherwise the gate service explains what is still due
            var result = await gateService.CheckoutAsync(request.SessionId!.Value, new CheckoutModel
            {
                GateId = model.GateId,
                PlateNumber = request.PlateNumber,
                PhotoChecked = model.PlateConfirmed,
                AllowPlateMismatch = model.AllowPlateMismatch,
                OverrideReason = model.OverrideReason
            });
            decision.CheckOut = result;
            decision.BarrierOpen = result.BarrierOpen;
            decision.Alerts = result.Alerts.Where(a => a.Blocking).ToList();
        }

        if (decision.BarrierOpen)
        {
            // The gate service may have saved through the same context; reload the request before closing it
            var tracked = await pmsContext.GateRequests.FirstAsync(g => g.Id == request.Id);
            tracked.SessionId = request.SessionId;
            Close(tracked, GateRequestStatus.Completed, lotScope.UserId, null);
            await pmsContext.SaveChangesAsync();
            request = tracked;
            await PushAsync(request);
        }

        decision.Request = (await ToModelsAsync([request]))[0];
        return decision;
    }

    public async Task<GateRequestModel> RefuseAsync(long requestId, RefuseGateRequestModel model)
    {
        if (string.IsNullOrWhiteSpace(model?.Reason))
            throw new BadRequestException("Give the customer a reason for refusing.");

        var request = await WaitingForStaffAsync(requestId);
        Close(request, GateRequestStatus.Refused, lotScope.UserId, model.Reason.Trim());
        await pmsContext.SaveChangesAsync();
        await PushAsync(request);
        return (await ToModelsAsync([request]))[0];
    }

    /* ------------------------------------------------------------------ helpers */

    private async Task<GateRequest> WaitingForStaffAsync(long requestId)
    {
        var request = await pmsContext.GateRequests.FirstOrDefaultAsync(g => g.Id == requestId)
                      ?? throw new NotFoundException("Request not found.");
        await lotScope.EnsureCanOperateAsync(request.ParkingLotId);
        if (request.Status != GateRequestStatus.Waiting)
            throw new BadRequestException(request.Status == GateRequestStatus.Cancelled
                ? "The customer cancelled this request."
                : "This request was already handled.");
        return request;
    }

    private void Close(GateRequest request, string status, string by, string note)
    {
        request.Status = status;
        request.HandledAt = Now;
        request.HandledBy = by;
        request.Note = note;
    }

    /// <summary>Waiting requests whose booking or session moved on without them.</summary>
    private async Task<List<GateRequest>> StaleAsync(List<GateRequest> requests)
    {
        var reservationIds = requests.Where(r => r.Kind == GateRequestKind.CheckIn && r.ReservationId != null).Select(r => r.ReservationId!.Value).ToList();
        var sessionIds = requests.Where(r => r.Kind == GateRequestKind.CheckOut && r.SessionId != null).Select(r => r.SessionId!.Value).ToList();
        var reservationStatus = await pmsContext.Reservations.AsNoTracking().Where(r => reservationIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Status);
        var sessionStatus = await pmsContext.ParkingSessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Status);

        return requests.Where(r => r.Kind == GateRequestKind.CheckIn
                ? r.ReservationId == null || reservationStatus.GetValueOrDefault(r.ReservationId.Value) != ReservationStatus.Confirmed
                : r.SessionId == null || !SessionStatus.Inside.Contains(sessionStatus.GetValueOrDefault(r.SessionId.Value)))
            .ToList();
    }

    private async Task<int> CustomerIdAsync(string userId) =>
        await pmsContext.Customers.Where(c => c.UserId == userId).Select(c => (int?)c.Id).FirstOrDefaultAsync()
        ?? throw new NotFoundException("Customer profile not found.");

    private Task PushAsync(GateRequest request) =>
        notifier?.GateRequestChangedAsync(request.ParkingLotId, request.UserId, request.Id, request.Kind, request.Status) ?? Task.CompletedTask;

    private async Task<List<GateRequestModel>> ToModelsAsync(IReadOnlyCollection<GateRequest> requests)
    {
        if (requests.Count == 0)
            return [];

        var lotIds = requests.Select(r => r.ParkingLotId).Distinct().ToList();
        var vehicleIds = requests.Where(r => r.VehicleId != null).Select(r => r.VehicleId!.Value).Distinct().ToList();
        var customerIds = requests.Where(r => r.CustomerId != null).Select(r => r.CustomerId!.Value).Distinct().ToList();
        var reservationIds = requests.Where(r => r.ReservationId != null).Select(r => r.ReservationId!.Value).Distinct().ToList();
        var sessionIds = requests.Where(r => r.SessionId != null).Select(r => r.SessionId!.Value).Distinct().ToList();

        var lots = await pmsContext.ParkingLots.AsNoTracking().Where(l => lotIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name);
        var vehicles = await pmsContext.Vehicles.AsNoTracking().Include(v => v.VehicleType).Where(v => vehicleIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id);
        var customers = await pmsContext.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id);
        var reservations = await pmsContext.Reservations.AsNoTracking().Where(r => reservationIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id);
        var sessions = await pmsContext.ParkingSessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        var slotIds = reservations.Values.Where(r => r.SlotId != null).Select(r => r.SlotId!.Value)
            .Concat(sessions.Values.Where(s => s.SlotId != null).Select(s => s.SlotId!.Value)).Distinct().ToList();
        var slots = await pmsContext.Slots.AsNoTracking().Where(s => slotIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Code);

        return requests.Select(r =>
        {
            var vehicle = r.VehicleId != null ? vehicles.GetValueOrDefault(r.VehicleId.Value) : null;
            var customer = r.CustomerId != null ? customers.GetValueOrDefault(r.CustomerId.Value) : null;
            var reservation = r.ReservationId != null ? reservations.GetValueOrDefault(r.ReservationId.Value) : null;
            var session = r.SessionId != null ? sessions.GetValueOrDefault(r.SessionId.Value) : null;
            var slotId = session?.SlotId ?? reservation?.SlotId;

            return new GateRequestModel
            {
                Id = r.Id,
                Kind = r.Kind,
                Status = r.Status,
                ParkingLotId = r.ParkingLotId,
                LotName = lots.GetValueOrDefault(r.ParkingLotId),
                PlateNumber = r.PlateNumber ?? vehicle?.PlateNumber,
                VehicleLabel = vehicle == null ? null : string.Join(" ", new[] { vehicle.Brand, vehicle.Model }.Where(x => !string.IsNullOrWhiteSpace(x))),
                VehicleColor = vehicle?.Color,
                VehicleTypeId = vehicle?.VehicleTypeId,
                VehicleTypeName = vehicle?.VehicleType?.Name,
                CustomerName = customer?.FullName,
                CustomerPhone = customer?.Phone,
                ReservationId = reservation?.Id,
                ReservationCode = reservation?.ReservationCode,
                ReservationStatus = reservation?.Status,
                ArrivalTime = reservation?.StartTime,
                CheckInFrom = reservation?.StartTime.AddMinutes(-EarlyArrivalMinutes),
                CheckInUntil = reservation?.EndTime,
                SlotCode = slotId != null ? slots.GetValueOrDefault(slotId.Value) : null,
                SessionId = session?.Id,
                SessionCode = session?.SessionCode,
                SessionStatus = session?.Status,
                EntryTime = session?.EntryTime,
                PaidAmount = session?.PaidAmount ?? 0,
                ExitWindowEndsAt = session?.ExitWindowEndsAt,
                Problem = r.Status == GateRequestStatus.Waiting ? ProblemOf(r, reservation, session) : null,
                CreatedAt = r.CreatedAt,
                HandledAt = r.HandledAt,
                Note = r.Note
            };
        }).ToList();
    }

    /// <summary>What would stop the barrier opening right now, so the Staff sees it before pressing.</summary>
    private string ProblemOf(GateRequest request, Reservation reservation, ParkingSession session)
    {
        if (request.Kind == GateRequestKind.CheckIn)
        {
            if (reservation == null) return "The booking no longer exists.";
            if (reservation.Status != ReservationStatus.Confirmed) return $"The booking is {reservation.Status}.";
            if (Now < reservation.StartTime.AddMinutes(-EarlyArrivalMinutes)) return "Too early: the check-in window has not opened.";
            if (Now > reservation.EndTime) return "The hold has ended: only a walk-in entry is possible.";
            return null;
        }

        if (session == null) return "The parking session no longer exists.";
        if (session.Status != SessionStatus.Paid) return "Not paid yet: take the fee at the POS before opening the barrier.";
        if (session.ExitWindowEndsAt is { } windowEnd && Now > windowEnd) return "The exit window has passed: an extra fee is due.";
        return null;
    }
}
