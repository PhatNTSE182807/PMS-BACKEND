using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Admin demo traffic: walk-in vehicles with random plates go through the real entry lane (check-in,
/// sensor confirmation), stay a random time, then pay cash at the exit lane and leave. Zone counters, the 3D
/// map, fees and revenue all move as for real vehicles; the sessions carry <c>IsSimulated</c> so they can be
/// told apart. Nothing is ever deleted.
/// </summary>
public class WalkInSimulationService(
    PmsDbContext pmsContext,
    GateService gates,
    LotScopeService lotScope,
    ParkingOptions options,
    TimeProvider clock,
    BookedBayCaseService bookedBays = null)
{
    private const string AdminRole = "Admin";
    private const int MaxStayHours = 72;

    /// <summary>Province codes used on real plates (Hanoi, Ho Chi Minh City, Da Nang and a few neighbours).</summary>
    private static readonly int[] Provinces = [29, 30, 31, 43, 47, 49, 50, 51, 59, 60, 61, 62, 63, 72];

    /// <summary>Series letters as on real plates (no I, O, Q or W).</summary>
    private const string Letters = "ABCDEFGHKLMNPRSTUVXYZ";

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<DeployWalkInsResultModel> DeployAsync(DeployWalkInsModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.CarPercent is < 0 or > 100 || model.Car7Percent is < 0 or > 100 || model.NoPlatePercent is < 0 or > 100)
            throw new BadRequestException("Percentages must be between 0 and 100.");
        if (model.CarPercent + model.Car7Percent > 100)
            throw new BadRequestException("4-seat and 7-seat cars together cannot be more than 100%.");
        if (model.MinStayHours <= 0 || model.MaxStayHours < model.MinStayHours || model.MaxStayHours > MaxStayHours)
            throw new BadRequestException($"The stay must be more than 0 and at most {MaxStayHours} hours, the shortest no longer than the longest.");

        await EnsureOpenAsync(model.LotId);
        // Each vehicle parks in a real free bay (as when Staff picks one at the lane), so lists and the 3D map show where it is
        var freeBays = await FreeBaysAsync(model.LotId);
        // The batch is capped by the lot itself: never more vehicles than it has free bays
        if (freeBays.Count == 0)
            throw new BadRequestException("No free bay is left in this lot.");
        if (model.Count < 1 || model.Count > freeBays.Count)
            throw new BadRequestException($"This lot has {freeBays.Count} free bays: send between 1 and {freeBays.Count} vehicles.");

        var types = await pmsContext.VehicleTypes.AsNoTracking().Where(t => t.IsActive).ToListAsync();
        var car4 = types.FirstOrDefault(t => t.Code == "CAR_4");
        var car7 = types.FirstOrDefault(t => t.Code == "CAR_7");
        var motorbike = types.FirstOrDefault(t => t.Code == "MOTORBIKE");
        if (car4 == null && car7 == null && motorbike == null)
            throw new BadRequestException("No vehicle type is active.");

        var adminId = lotScope.UserId;
        var result = new DeployWalkInsResultModel { Requested = model.Count };
        var taken = new HashSet<string>();
        var typesById = types.ToDictionary(t => t.Id);
        for (var i = 0; i < model.Count; i++)
        {
            var type = PickType(model, car4, car7, motorbike);
            var isCar = type != motorbike;
            var noPlate = RandomNumberGenerator.GetInt32(100) < model.NoPlatePercent;
            var plate = noPlate ? null : await FreePlateAsync(isCar, taken);
            var bay = PickBay(freeBays, typesById, type.Id);
            if (bay == null)
            {
                result.StoppedReason = $"No free {(isCar ? "car" : "motorbike")} bay is left in this lot.";
                break;
            }
            freeBays.RemoveAll(b => b.Slot.Id == bay.Id);

            var entry = await gates.CheckInAsync(model.LotId, new CheckInModel
            {
                PlateNumber = plate,
                NoPlate = noPlate,
                NoPlateReason = noPlate ? "Simulated: the plate could not be read." : null,
                VehicleTypeId = type.Id,
                SlotId = bay.Id,
                PlateConfirmed = true,
                IgnoreReservation = true
            });
            if (entry.Session == null)
            {
                result.StoppedReason = string.Join(" ", entry.Alerts.Where(a => a.Blocking).Select(a => a.Message));
                break;
            }

            await gates.ConfirmEntryAsync(entry.Session.Id);
            var session = await pmsContext.ParkingSessions.FirstAsync(s => s.Id == entry.Session.Id);
            session.IsSimulated = true;
            session.SimulatedBy = adminId;
            session.SimulatedExitAt = Now + StayLength(model.MinStayHours, model.MaxStayHours);
            await pmsContext.SaveChangesAsync();
            result.Entered++;
        }

        return result;
    }

    public async Task<WalkInSimulationOverviewModel> GetOverviewAsync(int? lotId)
    {
        var query = pmsContext.ParkingSessions.AsNoTracking().Where(s => s.IsSimulated);
        if (lotId != null)
            query = query.Where(s => s.ParkingLotId == lotId);

        var inside = await query
            .Where(s => SessionStatus.Open.Contains(s.Status))
            .OrderByDescending(s => s.EntryTime)
            .Select(s => new SimulatedWalkInModel
            {
                SessionId = s.Id,
                SessionCode = s.SessionCode,
                LotId = s.ParkingLotId,
                LotName = s.ParkingLot.Name,
                Plate = s.EntryPlateNumber,
                HasPlate = s.HasPlate,
                VehicleTypeName = pmsContext.VehicleTypes.Where(t => t.Id == s.VehicleTypeId).Select(t => t.Name).FirstOrDefault(),
                ZoneName = pmsContext.Zones.Where(z => z.Id == s.ZoneId).Select(z => z.Name).FirstOrDefault(),
                EntryTime = s.EntryTime,
                PlannedExitAt = s.SimulatedExitAt,
                Status = s.Status,
                LostTicket = s.IsLostCard
            })
            .ToListAsync();
        // Stored in UTC: say so, so clients show local times
        foreach (var vehicle in inside)
        {
            vehicle.EntryTime = DateTime.SpecifyKind(vehicle.EntryTime, DateTimeKind.Utc);
            vehicle.PlannedExitAt = vehicle.PlannedExitAt is { } exit ? DateTime.SpecifyKind(exit, DateTimeKind.Utc) : null;
        }

        var completed = query.Where(s => s.Status == SessionStatus.Completed);
        return new WalkInSimulationOverviewModel
        {
            Inside = inside,
            CompletedCount = await completed.CountAsync(),
            Revenue = await completed.SumAsync(s => (decimal?)s.FinalFee) ?? 0,
            TotalBays = lotId is { } id
                ? await pmsContext.Slots.CountAsync(s => s.Zone.ParkingLotId == id && s.Zone.IsActive && s.IsActive)
                : null,
            FreeBays = lotId is { } lot ? (await FreeBaysAsync(lot)).Count : null
        };
    }

    /// <summary>Drives one simulated vehicle out now: pays what is due in cash at the exit lane, then leaves.</summary>
    public async Task<SimulatedWalkInModel> CheckOutNowAsync(long sessionId)
    {
        var session = await SimulatedSessionAsync(sessionId);
        await LeaveAsync(session.Id, session.HasPlate ? session.EntryPlateNumber : null);
        return (await GetOverviewAsync(session.ParkingLotId)).Inside.FirstOrDefault(s => s.SessionId == sessionId);
    }

    /// <summary>Drives every simulated vehicle of the lot (or of every lot) out now.</summary>
    public async Task<WalkInCheckOutResultModel> CheckOutAllAsync(int? lotId)
    {
        var due = await pmsContext.ParkingSessions.AsNoTracking()
            .Where(s => s.IsSimulated && SessionStatus.Inside.Contains(s.Status) && (lotId == null || s.ParkingLotId == lotId))
            .Select(s => new { s.Id, s.HasPlate, s.EntryPlateNumber })
            .ToListAsync();
        return await LeaveEachAsync(due.Select(s => (s.Id, s.HasPlate ? s.EntryPlateNumber : null, (string)null)));
    }

    /// <summary>
    /// Background run (every minute, and every second while the parking simulation is on): vehicles whose stay
    /// is over pay and leave, on behalf of the Admin who sent them in.
    /// </summary>
    public async Task<WalkInCheckOutResultModel> CheckOutDueAsync()
    {
        var due = await pmsContext.ParkingSessions.AsNoTracking()
            .Where(s => s.IsSimulated && s.SimulatedExitAt <= Now
                        && (s.Status == SessionStatus.Active || s.Status == SessionStatus.PendingPayment || s.Status == SessionStatus.Paid))
            .OrderBy(s => s.SimulatedExitAt)
            .Select(s => new { s.Id, s.HasPlate, s.EntryPlateNumber, s.SimulatedBy })
            .Take(200)
            .ToListAsync();
        return await LeaveEachAsync(due.Select(s => (s.Id, s.HasPlate ? s.EntryPlateNumber : null, s.SimulatedBy)));
    }

    /// <summary>The driver of a simulated vehicle says the ticket is lost; Staff approves on the spot or a Manager decides.</summary>
    public async Task<SimulatedWalkInModel> ReportLostTicketAsync(long sessionId, SimulateLostTicketModel model)
    {
        var session = await SimulatedSessionAsync(sessionId);
        await gates.ReportLostTicketAsync(session.Id, new LostTicketModel
        {
            Reason = "Simulated: the driver lost the paper ticket.",
            ConditionsMet = model?.ConditionsMet == true
        });
        return (await GetOverviewAsync(session.ParkingLotId)).Inside.FirstOrDefault(s => s.SessionId == sessionId);
    }

    /// <summary>
    /// A walk-in parks in the bay of a confirmed booking (BR-08 exception). When that customer checks in, the
    /// entry lane moves the booking to another bay with the first hour free, or to the walk-in area with a full refund.
    /// </summary>
    public async Task<BookedBayTakenModel> OccupyBookedBayAsync(int lotId, long? reservationId)
    {
        await EnsureOpenAsync(lotId);
        var occupied = pmsContext.ParkingSessions.Where(s => s.SlotId != null && SessionStatus.Open.Contains(s.Status)).Select(s => s.SlotId);
        var booking = await pmsContext.Reservations.AsNoTracking()
            .Where(r => r.ParkingLotId == lotId && r.Status == ReservationStatus.Confirmed && r.SlotId != null
                        && (reservationId == null || r.Id == reservationId) && !occupied.Contains(r.SlotId))
            .OrderBy(r => r.StartTime)
            .FirstOrDefaultAsync()
            ?? throw new BadRequestException("No confirmed booking in this lot has a free bay to take. Book a bay first.");

        var bay = await pmsContext.Slots.AsNoTracking().FirstAsync(s => s.Id == booking.SlotId);
        var vehicleTypeId = booking.VehicleTypeId ?? bay.VehicleTypeId;
        var isCar = await pmsContext.VehicleTypes.AnyAsync(t => t.Id == vehicleTypeId && t.Code.StartsWith("CAR"));
        var plate = await FreePlateAsync(isCar, []);

        var entry = await gates.CheckInAsync(lotId, new CheckInModel
        {
            PlateNumber = plate,
            VehicleTypeId = vehicleTypeId,
            PlateConfirmed = true,
            IgnoreReservation = true,
            AllowWhenFull = true,
            OverrideReason = "Simulated: a walk-in drove into a booked bay."
        });
        if (entry.Session == null)
            throw new BadRequestException(string.Join(" ", entry.Alerts.Where(a => a.Blocking).Select(a => a.Message)));

        await gates.ConfirmEntryAsync(entry.Session.Id);
        // The driver ignored the signs and parked in the booked bay: the session shows where the vehicle really is
        var session = await pmsContext.ParkingSessions.FirstAsync(s => s.Id == entry.Session.Id);
        session.SlotId = bay.Id;
        session.ZoneId = bay.ZoneId;
        session.IsSimulated = true;
        session.SimulatedBy = lotScope.UserId;
        session.SimulatedExitAt = Now + StayLength(2, 4);
        await pmsContext.SaveChangesAsync();

        // Staff get the case in Exceptions & Alerts, the booking's customer a "your bay is taken" notification
        var bayCase = bookedBays == null ? null : await bookedBays.OpenIfBookedAsync(session.Id);

        return new BookedBayTakenModel
        {
            SessionId = session.Id,
            Plate = plate,
            BayCode = bay.Code,
            ReservationId = booking.Id,
            ReservationCode = booking.ReservationCode,
            CaseId = bayCase?.Id
        };
    }

    // ---- helpers ----------------------------------------------------------------------------------

    /// <summary>
    /// The vehicle type of the next walk-in from the batch's shares (4-seat, 7-seat, the rest motorbikes); when a
    /// type is not active, the nearest one stands in.
    /// </summary>
    private static VehicleType PickType(DeployWalkInsModel model, VehicleType car4, VehicleType car7, VehicleType motorbike)
    {
        var roll = RandomNumberGenerator.GetInt32(100);
        var wantsCar = roll < model.CarPercent + model.Car7Percent;
        var wanted = roll < model.CarPercent ? car4 : wantsCar ? car7 : motorbike;
        if (wanted != null)
            return wanted;
        // A car stands in for the other car size; a motorbike only when no car type is active
        return wantsCar ? car4 ?? car7 ?? motorbike : car4 ?? car7;
    }

    /// <summary>
    /// Bays a walk-in may park in, by the same rule as the entry lane (never a bay with a vehicle or a booking due
    /// soon), bays without any booking first.
    /// </summary>
    private Task<List<ParkingInventory.WalkInBay>> FreeBaysAsync(int lotId) =>
        new ParkingInventory(pmsContext).WalkInBaysAsync(lotId, null, Now, TimeSpan.FromMinutes(options.Reservations.HoldAheadMinutes));

    /// <summary>
    /// A random free bay that takes this vehicle type (cars share bays) and has no booking; only when none is left,
    /// the bay whose booking is furthest ahead. Null when no bay fits.
    /// </summary>
    private static Slot PickBay(List<ParkingInventory.WalkInBay> freeBays, IReadOnlyDictionary<int, VehicleType> types, int vehicleTypeId)
    {
        var fitting = freeBays.Where(b => ParkingInventory.Compatible(types, b.Slot.VehicleTypeId, vehicleTypeId)).ToList();
        var unbooked = fitting.Where(b => b.NextBookingAt == null).ToList();
        if (unbooked.Count > 0)
            return unbooked[RandomNumberGenerator.GetInt32(unbooked.Count)].Slot;
        // Already ordered with the furthest booking first
        return fitting.FirstOrDefault()?.Slot;
    }

    /// <summary>
    /// Simulated traffic only goes to lots that are open: not under maintenance, closed, or open without a
    /// Manager and Staff on duty (those show as maintenance to everyone).
    /// </summary>
    private async Task EnsureOpenAsync(int lotId)
    {
        var lot = await pmsContext.ParkingLots.AsNoTracking().FirstOrDefaultAsync(p => p.Id == lotId && !p.IsDeleted)
                  ?? throw new NotFoundException("Parking lot not found.");
        var status = LotStaffing.EffectiveStatus(lot, await LotStaffing.IsStaffedAsync(pmsContext, lotId));
        if (status != LotStatus.Active)
            throw new BadRequestException(status == LotStatus.Maintenance
                ? $"{lot.Name} is under maintenance: simulated vehicles cannot enter it."
                : $"{lot.Name} is not open: simulated vehicles cannot enter it.");
    }

    private async Task<WalkInCheckOutResultModel> LeaveEachAsync(IEnumerable<(long Id, string Plate, string ActAs)> sessions)
    {
        var result = new WalkInCheckOutResultModel();
        foreach (var (id, plate, actAs) in sessions)
        {
            if (actAs != null)
                lotScope.ActAs(actAs, AdminRole);
            try
            {
                await LeaveAsync(id, plate);
                result.CheckedOut++;
            }
            catch (Exception e) when (e is BadRequestException or ConflictException or ForbiddenException or NotFoundException)
            {
                // A lost ticket waiting for a Manager, or a case that was rejected: the vehicle stays for now
                pmsContext.ChangeTracker.Clear();
                result.Skipped++;
            }
        }
        return result;
    }

    private async Task LeaveAsync(long sessionId, string plate)
    {
        var quote = await gates.RequestPaymentAsync(sessionId, new CheckoutQuoteRequestModel { PlateNumber = plate });
        if (quote.AmountDue > 0)
            await gates.PayCashAsync(sessionId, new CashPaymentModel { Amount = quote.AmountDue });
        await gates.CheckoutAsync(sessionId, new CheckoutModel { PlateNumber = plate, PhotoChecked = true });
    }

    private async Task<ParkingSession> SimulatedSessionAsync(long sessionId) =>
        await pmsContext.ParkingSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId && s.IsSimulated)
        ?? throw new NotFoundException("No simulated vehicle with this session.");

    /// <summary>
    /// A random plate in the real format that is not parked now, not a registered vehicle (it would be linked
    /// to that customer's account) and not on a blacklist or whitelist.
    /// </summary>
    private async Task<string> FreePlateAsync(bool car, HashSet<string> taken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var plate = RandomPlate(car);
            var normalized = ParkingText.NormalizePlate(plate);
            if (!taken.Add(normalized)) continue;

            var used = await pmsContext.ParkingSessions.AnyAsync(s => s.EntryPlateNormalized == normalized && SessionStatus.Open.Contains(s.Status))
                       || await pmsContext.Vehicles.AnyAsync(v => v.PlateNormalized == normalized)
                       || await pmsContext.PlateLists.AnyAsync(p => p.PlateNormalized == normalized);
            if (!used) return plate;
        }
        throw new ConflictException("Could not find a free random plate; try again.");
    }

    /// <summary>"51K-123.45" for a car, "59X1-234.56" for a motorbike.</summary>
    public static string RandomPlate(bool car)
    {
        var province = Provinces[RandomNumberGenerator.GetInt32(Provinces.Length)];
        var letter = Letters[RandomNumberGenerator.GetInt32(Letters.Length)];
        var series = car ? $"{letter}" : $"{letter}{RandomNumberGenerator.GetInt32(1, 10)}";
        var number = RandomNumberGenerator.GetInt32(0, 100000).ToString("D5");
        return $"{province}{series}-{number[..3]}.{number[3..]}";
    }

    /// <summary>A random stay between the bounds. While the parking simulation is on, one hour lasts one pricing block.</summary>
    private TimeSpan StayLength(double minHours, double maxHours)
    {
        var hours = minHours + (maxHours - minHours) * RandomNumberGenerator.GetInt32(0, 1001) / 1000d;
        return options.Simulation is { Enabled: true } simulation
            ? TimeSpan.FromSeconds(Math.Max(1, hours * simulation.BlockSeconds))
            : TimeSpan.FromHours(hours);
    }
}
