using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Helpers.DemoData;

/// <summary>What is going on right now: vehicles inside, open lost-ticket cases, bookings about to arrive, app requests.</summary>
public sealed partial class DemoDataSeed
{
    private async Task SeedCurrentStateAsync()
    {
        var (fptu, fsoft, conghoa) = (lots["fptu"], lots["fsoft"], lots["conghoa"]);
        var (an, binh, em, giang, hai) = (customers["an"], customers["binh"], customers["em"], customers["giang"], customers["hai"]);

        // FPT University ---------------------------------------------------------------------------
        // A verified car parked as a walk-in: linked to An's account, payable in the app
        await ParkAsync(fptu, "51K-123.45", Car4, an, HoursAgo(3.17), withSlot: true);
        // Lost ticket waiting for the Staff to check the driver's papers (the Staff decides lost tickets, not the Manager)
        var lostTicket = await ParkAsync(fptu, "51G-999.01", Car4, em, HoursAgo(5), withSlot: false);
        await ReportLostTicketAsync(lostTicket, fptu, employees["staff.fptu1"].Id, HoursAgo(0.25),
            "The driver lost the paper ticket; the registration papers are in the car.");
        await ParkAsync(fptu, "51D-357.91", Car4, null, HoursAgo(1.08), withSlot: true);
        // Over 24 hours: the overdue job alerts the lot
        await ParkAsync(fptu, "51B-468.24", Car7, null, HoursAgo(26.4), withSlot: true);
        await ParkAsync(fptu, "59P1-111.11", Motorbike, null, HoursAgo(2.1), withSlot: false);
        // No plate: the exit asks Staff to compare the photos
        await ParkAsync(fptu, string.Empty, Motorbike, null, HoursAgo(0.66), withSlot: false);
        // Whitelisted shuttle: free of charge at the exit
        await ParkAsync(fptu, "51A-000.01", Car4, null, HoursAgo(4.2), withSlot: false, SessionType.Whitelist);

        // Fsoft --------------------------------------------------------------------------------------
        // Parked on a booking: the deposit is deducted at check-out
        var binhCar = VehicleOf(binh, "30G-555.88");
        var binhBooking = await BookAsync(binh, binhCar, fsoft, await TakeFreeSlotAsync(ZonesFor(fsoft, Car4)[0]),
            HoursAgo(3), HoursAgo(2), withDeposit: true, holdSlot: true);
        await ParkAsync(fsoft, binhCar.PlateNumber, Car4, binh, HoursAgo(1.92), withSlot: true, reservation: binhBooking);

        // Asked to leave from the app: a check-out request waits in the lane queue
        var haiBike = await ParkAsync(fsoft, "59C1-468.02", Motorbike, hai, HoursAgo(0.83), withSlot: false);
        db.GateRequests.Add(new GateRequest
        {
            ParkingLotId = fsoft.Id,
            Kind = GateRequestKind.CheckOut,
            Status = GateRequestStatus.Waiting,
            UserId = hai.UserId,
            CustomerId = hai.Customer.Id,
            SessionId = haiBike.Id,
            VehicleId = haiBike.VehicleId,
            PlateNumber = haiBike.EntryPlateNumber,
            CreatedAt = HoursAgo(0.04)
        });

        // Parked 8 days: abandoned (the job opens an incident); also a ghost session a Manager can close
        await ParkAsync(fsoft, "61A-135.24", Car4, null, HoursAgo(8 * 24 + 3), withSlot: true);
        // Lost-ticket case rejected: nobody can take the fee until a new case is approved
        var rejected = await ParkAsync(fsoft, "51E-246.80", Car7, null, HoursAgo(6.1), withSlot: false);
        await ReportLostTicketAsync(rejected, fsoft, employees["staff.fsoft2"].Id, HoursAgo(3),
            "Driver says the ticket blew away at the payment kiosk.",
            decidedBy: employees["staff.fsoft2"].Id, decidedAt: HoursAgo(2.7),
            decisionNote: "The driver could not show the registration papers. Come back with them and report again.");

        // An arrives in 10 minutes on a paid booking and already asked the lane to open from the app
        var anMpv = VehicleOf(an, "51H-678.90");
        var upcoming = await BookAsync(an, anMpv, fsoft, await TakeFreeSlotAsync(ZonesFor(fsoft, Car4)[0]),
            HoursAgo(0.42), now.AddMinutes(10), withDeposit: true, holdSlot: true);
        db.GateRequests.Add(new GateRequest
        {
            ParkingLotId = fsoft.Id,
            Kind = GateRequestKind.CheckIn,
            Status = GateRequestStatus.Waiting,
            UserId = an.UserId,
            CustomerId = an.Customer.Id,
            ReservationId = upcoming.Id,
            VehicleId = anMpv.Id,
            PlateNumber = anMpv.PlateNumber,
            CreatedAt = HoursAgo(0.02)
        });

        // Cong Hoa -----------------------------------------------------------------------------------
        // Paid in the app a few minutes ago: the barrier opens until the exit window ends, then a late fee applies
        var prepaid = await ParkAsync(conghoa, "51L-202.02", Car4, giang, HoursAgo(2.5), withSlot: true);
        await PrepayFromWalletAsync(prepaid, conghoa, giang, HoursAgo(0.08));
        await ParkAsync(conghoa, "59T1-777.77", Motorbike, null, HoursAgo(3.05), withSlot: false);

        // An's second active booking (the limit is two): tomorrow 08:00-11:00, no deposit, bay not locked yet
        var arrivalLocal = ToLocal(now).Date.AddDays(1).AddHours(8);
        var arrival = ToUtc(arrivalLocal);
        if (arrival < now.AddHours(3))
            arrival = ToUtc(arrivalLocal.AddDays(1));
        await BookAsync(an, VehicleOf(an, "51K-123.45"), conghoa, await TakeFreeSlotAsync(ZonesFor(conghoa, Car4)[0]),
            HoursAgo(1.5), arrival, withDeposit: false, holdSlot: true, BookingMode.Scheduled, arrival.AddHours(3));

        await db.SaveChangesAsync();
    }

    private string Operator(LotInfo lot, int daysAgo)
    {
        var staff = lot.StaffOn(daysAgo);
        return staff.Count > 0 ? staff[random.Next(staff.Count)] : lot.ManagerId ?? adminId;
    }

    /// <summary>
    /// A vehicle inside the lot (entry confirmed by the sensor). Walk-ins go to the zone with the most free
    /// spaces, on a bay of their own when <paramref name="withSlot"/>; a booking parks on its held bay and
    /// becomes CheckedIn. The zone counter and the bay move as ParkingInventory.ParkAsync moves them.
    /// </summary>
    private async Task<ParkingSession> ParkAsync(LotInfo lot, string plate, string typeCode, CustomerInfo owner, DateTime entry,
        bool withSlot, string sessionType = SessionType.WalkIn, Reservation reservation = null)
    {
        Zone zone;
        Slot slot = null;
        if (reservation != null)
        {
            zone = lot.Zones.First(z => z.Id == reservation.ZoneId);
            slot = zone.Slots.First(s => s.Id == reservation.SlotId);
        }
        else
        {
            zone = WalkInZone(lot, typeCode);
            if (withSlot)
                slot = await TakeFreeSlotAsync(zone);
        }

        var vehicle = owner == null ? null : VehicleOf(owner, plate);
        var tariff = await tariffs.ForAsync(lot.Id, reservation != null ? zone.Id : null, types[typeCode].Id, entry);
        var operatorId = Operator(lot, 0);
        var session = NewSession(lot, plate, typeCode, entry, reservation != null ? SessionType.Reservation : sessionType,
            owner, vehicle, operatorId, zone, slot, tariff, reservation);
        db.ParkingSessions.Add(session);
        await inventory.ParkAsync(zone.Id, slot?.Id, lot.Id);
        await db.SaveChangesAsync();

        if (reservation != null)
        {
            var confirmedAt = session.EntryConfirmedAt!.Value;
            reservation.Status = ReservationStatus.CheckedIn;
            reservation.CheckedInAt = confirmedAt;
            reservation.UpdatedAt = confirmedAt;
            LogReservation(reservation, "CheckedIn", ReservationStatus.Confirmed, session.SessionCode, operatorId, confirmedAt);
            await db.SaveChangesAsync();
        }

        MarkBusy(plate, entry, DateTime.MaxValue);
        if (owner != null)
            customerSessions.Add(session);
        return session;
    }

    /// <summary>
    /// A lost-ticket report as GateService.ReportLostTicketAsync writes it (waiting for the Staff's check), or, with
    /// <paramref name="decidedBy"/>, the same case rejected by that Staff (the vehicle stays, IsLostCard set).
    /// </summary>
    private async Task ReportLostTicketAsync(ParkingSession session, LotInfo lot, string reporterId, DateTime reportedAt, string note,
        string decidedBy = null, DateTime? decidedAt = null, string decisionNote = null)
    {
        var tariff = await tariffs.ForAsync(lot.Id, session.ReservationId != null ? session.ZoneId : null, session.VehicleTypeId, session.EntryTime);
        var lostCase = new SessionViolation
        {
            SessionId = session.Id,
            ViolationType = ViolationType.LostTicket,
            FineAmount = tariff.LostTicketFee,
            Note = note,
            ReportedBy = reporterId,
            ReportedAt = reportedAt,
            Status = LostTicketCaseStatus.Pending,
            ApprovalMode = LostTicketApproval.Staff
        };
        session.IsLostCard = true;
        session.Status = SessionStatus.LostTicketPending;
        session.UpdatedAt = reportedAt;

        if (decidedBy != null)
        {
            lostCase.Status = LostTicketCaseStatus.Rejected;
            lostCase.DecidedBy = decidedBy;
            lostCase.DecidedAt = decidedAt;
            lostCase.DecisionNote = decisionNote;
            lostCase.IsResolved = true;
            session.Status = SessionStatus.Active;
            session.UpdatedAt = decidedAt;
        }

        db.SessionViolations.Add(lostCase);
        await db.SaveChangesAsync();
    }

    /// <summary>Paid from the wallet in the app (PaymentService wallet path): Paid, with an exit window from the payment.</summary>
    private async Task PrepayFromWalletAsync(ParkingSession session, LotInfo lot, CustomerInfo owner, DateTime paidAt)
    {
        var tariff = await tariffs.ForAsync(lot.Id, null, session.VehicleTypeId, session.EntryTime);
        var fee = Quote(tariff, session, paidAt);
        session.TotalDurationMinutes = fee.DurationMinutes;
        session.OriginalFee = fee.Total;
        session.FinalFee = fee.Total;
        PayFee(session, owner, fee.Total, paidAt, PaymentMethod.Wallet, null, PaymentChannel.App, null);
        session.PaidAmount = fee.Total;
        session.Status = SessionStatus.Paid;
        session.ExitWindowEndsAt = paidAt.AddMinutes(options.Gate.ExitWindowMinutes);
        session.UpdatedAt = paidAt;
        await db.SaveChangesAsync();
    }
}
