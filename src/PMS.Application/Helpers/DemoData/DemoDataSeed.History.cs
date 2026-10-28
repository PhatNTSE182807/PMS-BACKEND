using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Helpers.DemoData;

/// <summary>The last 30 days: finished visits and their payments, past bookings, and the unusual cases.</summary>
public sealed partial class DemoDataSeed
{
    private async Task SeedHistoryAsync()
    {
        var today = ToLocal(now).Date;

        // Fixed stories first, so random visits never overlap them for the same vehicle
        await AddPastReservationsAsync(today);
        await AddSpecialCasesAsync(today);
        await AddWhitelistVisitsAsync(today);

        for (var daysAgo = 30; daysAgo >= 0; daysAgo--)
        {
            var date = today.AddDays(-daysAgo);
            var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            foreach (var key in OperatingLots)
            {
                var visits = random.Next(4, 8) + (weekend ? 2 : 0);
                for (var i = 0; i < visits; i++)
                    await AddPastVisitAsync(lots[key], date, daysAgo);
            }
        }
    }

    /// <summary>One ordinary visit: a guest or (sometimes) a demo customer's verified vehicle, paid one of the usual ways.</summary>
    private async Task AddPastVisitAsync(LotInfo lot, DateTime date, int daysAgo)
    {
        var roll = random.NextDouble();
        var typeCode = roll < 0.5 ? Motorbike : roll < 0.85 ? Car4 : Car7;
        var entry = ToUtc(date.AddHours(random.Next(6, 21)).AddMinutes(random.Next(60)).AddSeconds(random.Next(60)));
        var minutes = typeCode == Motorbike ? random.Next(30, 600) : random.Next(20, 540);
        if (typeCode != Motorbike && random.NextDouble() < 0.06)
            minutes = random.Next(12 * 60, 20 * 60); // overnight: two calendar days, each capped
        var paidAt = entry.AddMinutes(minutes);
        if (paidAt > now.AddMinutes(-30))
            return;

        CustomerInfo owner = null;
        Vehicle vehicle = null;
        if (random.NextDouble() < 0.22)
            (owner, vehicle) = PickCustomerVehicle(typeCode, entry, paidAt.AddMinutes(20));
        if (vehicle != null)
            typeCode = types.Values.First(t => t.Id == vehicle.VehicleTypeId).Code;
        var plate = vehicle?.PlateNumber ?? RandomPlate(typeCode);

        string method, provider = null, channel;
        var how = random.NextDouble();
        if (owner == null)
            (method, provider, channel) = how < 0.72
                ? (PaymentMethod.Cash, null, PaymentChannel.Lane)
                : (PaymentMethod.Online, RandomProvider(), PaymentChannel.Lane);
        else if (owner.PaysWithWallet && how < 0.55)
            (method, channel) = (PaymentMethod.Wallet, PaymentChannel.App);
        else if (how < 0.75)
            (method, channel) = (PaymentMethod.Cash, PaymentChannel.Lane);
        else
            (method, provider, channel) = (PaymentMethod.Online, RandomProvider(), PaymentChannel.App);

        // Prepaid in the app: the driver leaves within the exit window; at the lane the barrier opens at once
        var exit = channel == PaymentChannel.App ? paidAt.AddMinutes(random.Next(3, 13)) : paidAt.AddSeconds(random.Next(30, 150));
        await AddCompletedSessionAsync(lot, plate, typeCode, owner, vehicle, entry, paidAt, exit, daysAgo, method, provider, channel);
    }

    private string RandomProvider() => random.NextDouble() < 0.6 ? PaymentProviders.VnPay : PaymentProviders.MoMo;

    /// <summary>A verified vehicle of a demo customer that fits the type and is free for the whole stay, or none.</summary>
    private (CustomerInfo, Vehicle) PickCustomerVehicle(string typeCode, DateTime from, DateTime to)
    {
        var family = ParkingInventory.Family(typeCode);
        var candidates = customers.Values
            .SelectMany(c => c.Verified.Select(v => (Customer: c, v.Vehicle, v.ApprovedAt)))
            .Where(c => c.ApprovedAt < from && FamilyOf(c.Vehicle.VehicleTypeId) == family && !IsBusy(c.Vehicle.PlateNumber, from, to))
            .ToList();
        if (candidates.Count == 0)
            return (null, null);
        var pick = candidates[random.Next(candidates.Count)];
        return (pick.Customer, pick.Vehicle);
    }

    /// <summary>
    /// A finished session: quoted and paid at <paramref name="paidAt"/> (the fee is fixed by that quote), out
    /// through the exit lane at <paramref name="exit"/>. A booking's deposit is deducted and any remainder
    /// refunded; a lost ticket adds its fine. Nothing is charged when the amount due is 0 (BR-09).
    /// </summary>
    private async Task<ParkingSession> AddCompletedSessionAsync(LotInfo lot, string plate, string typeCode, CustomerInfo owner, Vehicle vehicle,
        DateTime entry, DateTime paidAt, DateTime exit, int daysAgo, string method, string provider, string channel,
        string sessionType = SessionType.WalkIn, Reservation reservation = null, bool lostTicket = false)
    {
        var zone = reservation != null ? lot.Zones.First(z => z.Id == reservation.ZoneId) : ZonesFor(lot, typeCode)[random.Next(ZonesFor(lot, typeCode).Count)];
        var slot = reservation != null
            ? zone.Slots.First(s => s.Id == reservation.SlotId)
            : random.NextDouble() < 0.5 ? zone.Slots.ElementAt(random.Next(zone.Slots.Count)) : null;

        var tariff = await tariffs.ForAsync(lot.Id, reservation != null ? zone.Id : null, types[typeCode].Id, entry);
        var session = NewSession(lot, plate, typeCode, entry, sessionType, owner, vehicle, Operator(lot, daysAgo), zone, slot, tariff, reservation);
        if (reservation != null)
            session.SessionType = SessionType.Reservation;

        var fee = Quote(tariff, session, paidAt);
        var deposit = reservation == null ? 0 : reservation.DepositAmount - reservation.RefundAmount;
        var lostFee = lostTicket ? tariff.LostTicketFee : 0;
        var settlement = FeeCalculator.Settle(fee.Total, deposit, 0, false, 0, lostFee);

        session.Status = SessionStatus.Completed;
        session.IsLostCard = lostTicket;
        session.TotalDurationMinutes = fee.DurationMinutes;
        session.OriginalFee = fee.Total;
        session.FinalFee = settlement.Fee + settlement.LostTicketFee;
        session.DepositApplied = settlement.DepositApplied;
        session.ZeroFeeReason = session.SessionType == SessionType.Whitelist ? "Whitelist"
            : settlement.AmountDue == 0 && deposit > 0 ? "CoveredByDeposit"
            : null;
        session.ExitTime = exit;
        session.ExitGateId = lot.Exit.Id;
        session.ExitOperatorUserId = Operator(lot, daysAgo);
        session.ExitPlateNumber = session.HasPlate ? session.EntryPlateNumber : null;
        session.ExitPlateNormalized = session.HasPlate ? session.EntryPlateNormalized : string.Empty;
        session.ExitAnprConfidence = session.HasPlate ? AnprConfidence() : null;
        session.UpdatedAt = exit;
        db.ParkingSessions.Add(session);
        await db.SaveChangesAsync();

        if (settlement.AmountDue > 0)
        {
            PayFee(session, owner, settlement.AmountDue, paidAt, method, provider, channel,
                channel == PaymentChannel.App ? null : session.ExitOperatorUserId);
            session.PaidAmount = settlement.AmountDue;
            if (channel is PaymentChannel.App or PaymentChannel.Kiosk)
                session.ExitWindowEndsAt = paidAt.AddMinutes(options.Gate.ExitWindowMinutes);
        }

        if (reservation != null)
        {
            reservation.Status = ReservationStatus.Completed;
            reservation.CompletedAt = exit;
            reservation.UpdatedAt = exit;
            if (settlement.DepositRefund > 0)
            {
                var depositPayment = db.Payments.Local.First(p => p.ReservationId == reservation.Id && p.Purpose == PaymentPurpose.Deposit);
                RefundToWallet(depositPayment, owner, settlement.DepositRefund, exit, $"Deposit remainder refund {reservation.ReservationCode}", "System");
                reservation.RefundAmount += settlement.DepositRefund;
            }
        }

        MarkBusy(plate, entry, exit);
        if (owner != null)
            customerSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    /// <summary>Past bookings of the demo customers, one of each ending.</summary>
    private async Task AddPastReservationsAsync(DateTime today)
    {
        var (fptu, fsoft, conghoa) = (lots["fptu"], lots["fsoft"], lots["conghoa"]);
        var (an, binh, dung, hai) = (customers["an"], customers["binh"], customers["dung"], customers["hai"]);

        // Completed: arrived on time, the deposit was deducted, the rest paid from the wallet in the app
        var anCar = VehicleOf(an, "51K-123.45");
        var completed = await PastBookingAsync(an, anCar, fptu, today.AddDays(-9).AddHours(8.2), today.AddDays(-9).AddHours(9), withDeposit: true);
        await CheckInAndOutAsync(completed, fptu, an, anCar, today.AddDays(-9).AddHours(9.08), today.AddDays(-9).AddHours(14.33), 7,
            PaymentMethod.Wallet, null, PaymentChannel.App, 9);

        // Completed, scheduled 18:30-21:30, the rest paid in cash at the lane
        var binhCar = VehicleOf(binh, "30G-555.88");
        var arrival = today.AddDays(-14).AddHours(18.5);
        var scheduled = await PastBookingAsync(binh, binhCar, fsoft, today.AddDays(-14).AddHours(10), arrival, withDeposit: true,
            BookingMode.Scheduled, ToUtc(arrival.AddHours(3)));
        await CheckInAndOutAsync(scheduled, fsoft, binh, binhCar, arrival.AddMinutes(6), arrival.AddHours(2.67), 2,
            PaymentMethod.Cash, null, PaymentChannel.Lane, 14);

        // Cancelled by the customer more than 30 minutes ahead: the whole deposit back to the wallet
        var cancelled = await PastBookingAsync(binh, binhCar, conghoa, today.AddDays(-12).AddHours(20), today.AddDays(-11).AddHours(10), withDeposit: true);
        var cancelledAt = ToUtc(today.AddDays(-11).AddHours(7.5));
        EndReservation(cancelled, ReservationStatus.Cancelled, "Change of plans", "User", binh.UserId, cancelledAt);
        RefundToWallet(DepositOf(cancelled), binh, cancelled.DepositAmount, cancelledAt, $"Cancellation refund {cancelled.ReservationCode}", binh.UserId);
        cancelled.RefundAmount = cancelled.DepositAmount;

        // Cancelled by the lot: 100 % refund
        var anMpv = VehicleOf(an, "51H-678.90");
        var lotCancelled = await PastBookingAsync(an, anMpv, fsoft, today.AddDays(-6).AddHours(19), today.AddDays(-5).AddHours(8.5), withDeposit: true);
        var lotCancelledAt = ToUtc(today.AddDays(-5).AddHours(6.75));
        EndReservation(lotCancelled, ReservationStatus.LotCancelled, "Level 2 closed for an urgent ceiling repair", "Lot", fsoft.ManagerId, lotCancelledAt);
        RefundToWallet(DepositOf(lotCancelled), an, lotCancelled.DepositAmount, lotCancelledAt, $"Lot cancelled reservation {lotCancelled.ReservationCode}", fsoft.ManagerId);
        lotCancelled.RefundAmount = lotCancelled.DepositAmount;

        // No-show: not checked in before the hold ended, the deposit is kept
        var haiCar = VehicleOf(hai, "51A-864.20");
        var noShow = await PastBookingAsync(hai, haiCar, fptu, today.AddDays(-7).AddHours(7), today.AddDays(-7).AddHours(8), withDeposit: true);
        EndReservation(noShow, ReservationStatus.NoShow, "Not checked in during the hold", "System", null, noShow.EndTime);

        // Expired: a MoMo link was opened but the deposit never paid
        var dungCar = VehicleOf(dung, "43A-246.13");
        var expired = await PastBookingAsync(dung, dungCar, conghoa, today.AddDays(-3).AddHours(13), today.AddDays(-3).AddHours(15),
            withDeposit: true, payDeposit: false);
        var link = NewPayment(conghoa.Id, dung, dung.Customer.Id, expired.DepositAmount, expired.CreatedAt.AddMinutes(1),
            PaymentMethod.Online, PaymentProviders.MoMo, PaymentPurpose.Deposit, PaymentChannel.App, null);
        link.ReservationId = expired.Id;
        link.Invoice.ReservationId = expired.Id;
        link.Invoice.Status = "Unpaid";
        link.Invoice.PaidAt = null;
        link.Invoice.DueDate = expired.DepositDueAt;
        link.Status = PaymentStatus.Expired;
        link.PaidAt = null;
        link.GatewayTransactionNo = null;
        link.CreatedAt = expired.CreatedAt.AddMinutes(1);
        link.ExpiresAt = expired.DepositDueAt;
        link.UpdatedAt = expired.DepositDueAt;
        EndReservation(expired, ReservationStatus.Expired, "Deposit not paid in time", "System", null, expired.DepositDueAt!.Value);

        // Completed without a deposit (confirmed at once, 15-minute hold), paid online in the app
        var noDeposit = await PastBookingAsync(hai, haiCar, conghoa, today.AddDays(-2).AddHours(16), today.AddDays(-2).AddHours(17), withDeposit: false);
        await CheckInAndOutAsync(noDeposit, conghoa, hai, haiCar, today.AddDays(-2).AddHours(17.13), today.AddDays(-2).AddHours(19.67), 6,
            PaymentMethod.Online, PaymentProviders.VnPay, PaymentChannel.App, 2);

        await db.SaveChangesAsync();
    }

    private Payment DepositOf(Reservation reservation) =>
        db.Payments.Local.First(p => p.ReservationId == reservation.Id && p.Purpose == PaymentPurpose.Deposit && p.Status == PaymentStatus.Succeeded);

    /// <summary>
    /// A past booking on a car bay of the lot (times are local); it held its bay, which is free again now.
    /// The bay is one no other booking holds: the booking is briefly active while it is written, and the
    /// database allows one active hold per bay (IX_Reservations_SlotId_Held).
    /// </summary>
    private async Task<Reservation> PastBookingAsync(CustomerInfo customer, Vehicle vehicle, LotInfo lot, DateTime createdLocal, DateTime arrivalLocal,
        bool withDeposit, string mode = BookingMode.PayAsYouGo, DateTime? departure = null, bool payDeposit = true)
    {
        var free = ZonesFor(lot, Car4)[0].Slots
            .Where(s => s.IsActive && s.Status == SlotStatus.Available && !takenSlots.Contains(s.Id))
            .ToList();
        var slot = free[random.Next(free.Count)];
        takenSlots.Add(slot.Id);
        return await BookAsync(customer, vehicle, lot, slot, ToUtc(createdLocal), ToUtc(arrivalLocal), withDeposit, holdSlot: false, mode, departure, payDeposit);
    }

    /// <summary>The booked vehicle enters (CheckedIn) and later pays and leaves (Completed). Times are local.</summary>
    private async Task CheckInAndOutAsync(Reservation reservation, LotInfo lot, CustomerInfo owner, Vehicle vehicle, DateTime entryLocal,
        DateTime paidLocal, int exitAfterMinutes, string method, string provider, string channel, int daysAgo)
    {
        var typeCode = types.Values.First(t => t.Id == vehicle.VehicleTypeId).Code;
        var paidAt = ToUtc(paidLocal);
        var session = await AddCompletedSessionAsync(lot, vehicle.PlateNumber, typeCode, owner, vehicle, ToUtc(entryLocal), paidAt,
            paidAt.AddMinutes(exitAfterMinutes), daysAgo, method, provider, channel, reservation: reservation);
        LogReservation(reservation, "CheckedIn", ReservationStatus.Confirmed, session.SessionCode, session.EntryOperatorUserId, session.EntryConfirmedAt!.Value);
        reservation.CheckedInAt = session.EntryConfirmedAt;
        await db.SaveChangesAsync();
    }

    /// <summary>The cases an ordinary day does not have: lost tickets, a ghost session, a turn-around, a missed exit window.</summary>
    private async Task AddSpecialCasesAsync(DateTime today)
    {
        var (fptu, fsoft, conghoa) = (lots["fptu"], lots["fsoft"], lots["conghoa"]);

        // Lost ticket approved by the Manager (reported by Staff), fine paid in cash with the fee
        var reportedAt = ToUtc(today.AddDays(-6).AddHours(17.67));
        var paidAt = ToUtc(today.AddDays(-6).AddHours(17.87));
        var approved = await AddCompletedSessionAsync(fptu, RandomPlate(Car4), Car4, null, null, ToUtc(today.AddDays(-6).AddHours(8.17)),
            paidAt, paidAt.AddMinutes(3), 6, PaymentMethod.Cash, null, PaymentChannel.Lane, lostTicket: true);
        db.SessionViolations.Add(new SessionViolation
        {
            SessionId = approved.Id,
            ViolationType = ViolationType.LostTicket,
            FineAmount = approved.FinalFee - approved.OriginalFee,
            Note = "Customer lost the paper ticket.",
            ReportedBy = approved.ExitOperatorUserId,
            ReportedAt = reportedAt,
            Status = LostTicketCaseStatus.Approved,
            ApprovalMode = LostTicketApproval.Manager,
            DecidedBy = fptu.ManagerId,
            DecidedAt = reportedAt.AddMinutes(8),
            DecisionNote = "Registration papers and Citizen ID checked against the entry photos.",
            IsResolved = true
        });

        // Lost ticket approved on the spot by Staff (conditions met); the Manager has not reviewed it yet
        var spotAt = ToUtc(today.AddDays(-1).AddHours(18.08));
        var conditional = await AddCompletedSessionAsync(conghoa, RandomPlate(Motorbike), Motorbike, null, null, ToUtc(today.AddDays(-1).AddHours(7.33)),
            spotAt.AddMinutes(2), spotAt.AddMinutes(4), 1, PaymentMethod.Cash, null, PaymentChannel.Lane, lostTicket: true);
        db.SessionViolations.Add(new SessionViolation
        {
            SessionId = conditional.Id,
            ViolationType = ViolationType.LostTicket,
            FineAmount = conditional.FinalFee - conditional.OriginalFee,
            Note = "Ticket lost; photos, papers and Citizen ID match.",
            ReportedBy = conditional.ExitOperatorUserId,
            ReportedAt = spotAt,
            Status = LostTicketCaseStatus.Approved,
            ApprovalMode = LostTicketApproval.Conditional,
            DecidedBy = conditional.ExitOperatorUserId,
            DecidedAt = spotAt
        });

        // Ghost session closed by the Manager (abnormal close, no fee)
        var ghostEntry = ToUtc(today.AddDays(-4).AddHours(7.5));
        var ghostClosed = ToUtc(today.AddDays(-4).AddHours(22.17));
        var ghostPlate = RandomPlate(Car4);
        var ghostZone = WalkInZone(fsoft, Car4);
        var ghost = NewSession(fsoft, ghostPlate, Car4, ghostEntry, SessionType.WalkIn, null, null, Operator(fsoft, 4), ghostZone, null,
            await tariffs.ForAsync(fsoft.Id, null, types[Car4].Id, ghostEntry));
        ghost.Status = SessionStatus.AbnormalClosed;
        ghost.ClosedNote = "Ghost session: the car left during a barrier power cut (checked on the camera footage).";
        ghost.ExitTime = ghostClosed;
        ghost.ExitOperatorUserId = fsoft.ManagerId;
        ghost.UpdatedAt = ghostClosed;
        db.ParkingSessions.Add(ghost);
        MarkBusy(ghostPlate, ghostEntry, ghostClosed);

        // Turned around at the barrier: no sensor signal, cancelled, never counted
        var turnEntry = ToUtc(today.AddDays(-2).AddHours(10.25));
        var turned = NewSession(fptu, RandomPlate(Car4), Car4, turnEntry, SessionType.WalkIn, null, null, Operator(fptu, 2), WalkInZone(fptu, Car4), null,
            await tariffs.ForAsync(fptu.Id, null, types[Car4].Id, turnEntry));
        turned.Status = SessionStatus.Cancelled;
        turned.EntryConfirmedAt = null;
        turned.ClosedNote = "No sensor signal: the vehicle turned around";
        turned.UpdatedAt = turnEntry.AddMinutes(options.Gate.EntryConfirmTimeoutMinutes + 1);
        db.ParkingSessions.Add(turned);
        await db.SaveChangesAsync();

        await AddMissedExitWindowAsync(conghoa, today.AddDays(-3));
    }

    /// <summary>
    /// Prepaid in cash at the counter, but left 25 minutes after the 15-minute exit window: re-priced from the
    /// entry, what was paid subtracted, and the late-exit fee added (BR-10), paid at the exit lane.
    /// </summary>
    private async Task AddMissedExitWindowAsync(LotInfo lot, DateTime date)
    {
        var entry = ToUtc(date.AddHours(9));
        var prepaidAt = ToUtc(date.AddHours(12));
        var laneAt = prepaidAt.AddMinutes(39);
        var plate = RandomPlate(Car4);
        var tariff = await tariffs.ForAsync(lot.Id, null, types[Car4].Id, entry);
        var counterStaff = Operator(lot, 3);
        var session = NewSession(lot, plate, Car4, entry, SessionType.WalkIn, null, null, Operator(lot, 3), WalkInZone(lot, Car4), null, tariff);

        var first = Quote(tariff, session, prepaidAt);
        var atLane = Quote(tariff, session, laneAt);
        var settlement = FeeCalculator.Settle(atLane.Total, 0, first.Total, true, options.Gate.ExitWindowLateFee);

        session.Status = SessionStatus.Completed;
        session.TotalDurationMinutes = atLane.DurationMinutes;
        session.OriginalFee = atLane.Total;
        session.FinalFee = settlement.Fee + settlement.ExitWindowLateFee;
        session.PaidAmount = first.Total + settlement.AmountDue;
        session.ExitTime = laneAt.AddMinutes(1);
        session.ExitGateId = lot.Exit.Id;
        session.ExitOperatorUserId = Operator(lot, 3);
        session.ExitPlateNumber = session.EntryPlateNumber;
        session.ExitPlateNormalized = session.EntryPlateNormalized;
        session.ExitAnprConfidence = AnprConfidence();
        session.UpdatedAt = session.ExitTime;
        db.ParkingSessions.Add(session);
        await db.SaveChangesAsync();

        PayFee(session, null, first.Total, prepaidAt, PaymentMethod.Cash, null, PaymentChannel.Kiosk, counterStaff);
        PayFee(session, null, settlement.AmountDue, laneAt, PaymentMethod.Cash, null, PaymentChannel.Lane, session.ExitOperatorUserId);
        MarkBusy(plate, entry, session.ExitTime.Value);
        await db.SaveChangesAsync();
    }

    /// <summary>The whitelisted shuttle comes most working days and never pays.</summary>
    private async Task AddWhitelistVisitsAsync(DateTime today)
    {
        var fptu = lots["fptu"];
        for (var daysAgo = 3; daysAgo <= 20; daysAgo += 3)
        {
            var date = today.AddDays(-daysAgo);
            var entry = ToUtc(date.AddHours(7).AddMinutes(random.Next(10, 50)));
            var leave = ToUtc(date.AddHours(17).AddMinutes(random.Next(0, 40)));
            await AddCompletedSessionAsync(fptu, "51A-000.01", Car4, null, null, entry, leave, leave.AddSeconds(random.Next(20, 90)), daysAgo,
                PaymentMethod.Cash, null, PaymentChannel.Lane, SessionType.Whitelist);
        }
    }
}
