using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Notifications;
using PMS.Application.Models.Operations;
using PMS.Application.Models.VehicleDocuments;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Jobs;

/// <summary>
/// Customer notifications, run by Hangfire (see PMS.API): fire-and-forget jobs enqueued by
/// <see cref="IUserNotificationPublisher"/> once a registration is decided, a vehicle checks in / out or a lot's
/// price changes, and a daily clean-up of old read notifications. Each job reads the saved record, writes one
/// notification (never twice for the same event, so Hangfire retries are safe) and pushes it live.
/// </summary>
public class NotificationJobs(
    PmsDbContext pmsContext,
    TimeProvider clock,
    IUserNotificationNotifier notifier = null,
    ILogger<NotificationJobs> logger = null,
    ICustomerAudience audience = null)
{
    public const string PurgeJobId = "purge-user-notifications";

    /// <summary>Read notifications older than this are deleted by the daily job.</summary>
    public const int ReadRetentionDays = 60;

    /// <summary>Unread ones are kept longer, but not forever.</summary>
    public const int UnreadRetentionDays = 180;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Staff approved or rejected a vehicle registration.</summary>
    public async Task<bool> VehicleReviewedAsync(Guid verificationId)
    {
        var verification = await pmsContext.VehicleVerifications.AsNoTracking()
            .Include(v => v.Vehicle)
            .FirstOrDefaultAsync(v => v.Id == verificationId);
        if (verification == null || string.IsNullOrEmpty(verification.UserId))
            return false;

        var approved = VehicleVerificationStatus.Verified.Contains(verification.Status);
        if (!approved && verification.Status != VehicleVerificationStatus.Rejected)
            return false;

        var plate = verification.Vehicle?.PlateNumber ?? "your vehicle";
        var vehicleName = string.Join(' ', new[] { verification.Vehicle?.Brand, verification.Vehicle?.Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var data = new Dictionary<string, string>
        {
            ["plateNumber"] = verification.Vehicle?.PlateNumber,
            ["vehicleId"] = verification.VehicleId.ToString(),
            ["vehicleName"] = vehicleName,
            ["note"] = verification.ReviewNote
        };

        var label = string.IsNullOrEmpty(vehicleName) ? plate : $"{plate} ({vehicleName})";
        var notification = approved
            ? new UserNotification
            {
                Type = NotificationTypes.VehicleApproved,
                Title = "Vehicle registration approved",
                Message = $"Staff approved the papers of {label}. You can now book bays and it is recognised at the gates."
            }
            : new UserNotification
            {
                Type = NotificationTypes.VehicleRejected,
                Title = "Vehicle registration rejected",
                Message = $"Staff rejected the papers of {label}" +
                          (string.IsNullOrWhiteSpace(verification.ReviewNote) ? "." : $": \"{verification.ReviewNote.Trim()}\".") +
                          " The vehicle was removed from your account; register it again with new photos."
            };

        notification.UserId = verification.UserId;
        notification.RefType = "VehicleVerification";
        notification.RefId = verification.Id.ToString();
        notification.EventAt = verification.DecidedAt ?? Now;
        return await SaveAndPushAsync(notification, data);
    }

    /// <summary>The vehicle's entry was confirmed: tells the owner when and where it checked in.</summary>
    public async Task<bool> SessionCheckedInAsync(long sessionId)
    {
        var (session, userId, lot) = await LoadSessionAsync(sessionId);
        if (session == null || userId == null || !SessionStatus.Inside.Contains(session.Status) && session.Status != SessionStatus.Completed)
            return false;

        var at = session.EntryTime;
        var data = SessionData(session, lot);
        return await SaveAndPushAsync(new UserNotification
        {
            UserId = userId,
            Type = NotificationTypes.CheckIn,
            Title = $"Checked in at {lot?.Name ?? "the parking lot"}",
            Message = $"Vehicle {session.EntryPlateNumber} checked in at {LocalTime(at, lot)}. Ticket {session.SessionCode}.",
            RefType = "ParkingSession",
            RefId = session.Id.ToString(),
            EventAt = at
        }, data);
    }

    /// <summary>The vehicle left (or a Manager closed the session): check-out time, duration and fee.</summary>
    public async Task<bool> SessionCheckedOutAsync(long sessionId)
    {
        var (session, userId, lot) = await LoadSessionAsync(sessionId);
        if (session == null || userId == null || session.ExitTime == null)
            return false;

        var at = session.ExitTime.Value;
        var minutes = session.TotalDurationMinutes ?? (int)Math.Max(0, Math.Round((at - session.EntryTime).TotalMinutes));
        var fee = session.FinalFee > 0 ? session.FinalFee : session.PaidAmount;
        var data = SessionData(session, lot);
        data["exitTime"] = Iso(at);
        data["durationMinutes"] = minutes.ToString(CultureInfo.InvariantCulture);
        data["fee"] = fee.ToString("0.##", CultureInfo.InvariantCulture);

        var closedByManager = session.Status == SessionStatus.AbnormalClosed;
        if (closedByManager)
            data["note"] = session.ClosedNote;

        var message = $"Vehicle {session.EntryPlateNumber} checked out at {LocalTime(at, lot)} after {Duration(minutes)}"
                      + (fee > 0 ? $". Fee: {fee.ToString("N0", CultureInfo.InvariantCulture)} VND." : ".")
                      + (closedByManager ? $" The session was closed by the lot Manager{(string.IsNullOrWhiteSpace(session.ClosedNote) ? "" : $": {session.ClosedNote}")}." : "");

        return await SaveAndPushAsync(new UserNotification
        {
            UserId = userId,
            Type = NotificationTypes.CheckOut,
            Title = $"Checked out of {lot?.Name ?? "the parking lot"}",
            Message = message,
            RefType = "ParkingSession",
            RefId = session.Id.ToString(),
            EventAt = at
        }, data);
    }

    /// <summary>
    /// A lot's price for one vehicle type changed: one notification per customer. A customer with an upcoming booking
    /// for that type there gets <paramref name="title"/> / <paramref name="message"/> (check your booking); every other
    /// active customer gets <paramref name="allTitle"/> / <paramref name="allMessage"/> (the new price), for any customer
    /// may park at any lot. A checked-in booking does not count as upcoming, for its vehicle keeps the price it entered
    /// with. A Hangfire retry only adds the customers it missed. Returns the number of notifications written.
    /// </summary>
    public async Task<int> LotPriceChangedAsync(string title, string message, string eventKey, int lotId, int vehicleTypeId, string lotName,
        string allTitle = null, string allMessage = null)
    {
        var told = await pmsContext.UserNotifications
            .Where(n => n.Type == NotificationTypes.PriceChanged && n.RefId == eventKey)
            .Select(n => n.UserId)
            .ToListAsync();
        // No arrival-time filter: a late customer who has not entered yet still pays the price in force at entry
        var booked = await pmsContext.Reservations.AsNoTracking()
            .Where(r => r.ParkingLotId == lotId
                        && ReservationStatus.Upcoming.Contains(r.Status)
                        && r.UserId != null
                        // Older bookings have no type of their own: their vehicle's type decides
                        && (r.VehicleTypeId == vehicleTypeId
                            || (r.VehicleTypeId == null && pmsContext.Vehicles.Any(v => v.Id == r.VehicleId && v.VehicleTypeId == vehicleTypeId))))
            .Select(r => r.UserId)
            .Distinct()
            .ToListAsync();
        // Everyone else hears the new price itself (only when the general text was given and the accounts can be read)
        var others = audience != null && !string.IsNullOrWhiteSpace(allTitle) && !string.IsNullOrWhiteSpace(allMessage)
            ? (await audience.ActiveCustomerIdsAsync()).Except(booked).Except(told).ToList()
            : [];
        var bookedRecipients = booked.Except(told).ToList();
        var recipients = bookedRecipients.Concat(others).ToList();

        var dataJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["lotId"] = lotId.ToString(CultureInfo.InvariantCulture),
            ["lotName"] = lotName
        });

        var notifications = recipients.Select(userId => new UserNotification
        {
            UserId = userId,
            Type = NotificationTypes.PriceChanged,
            Title = bookedRecipients.Contains(userId) ? title : allTitle,
            Message = bookedRecipients.Contains(userId) ? message : allMessage,
            RefType = "LotPriceChange",
            RefId = eventKey,
            EventAt = Now,
            DataJson = dataJson,
            CreatedAt = Now
        }).ToList();
        pmsContext.UserNotifications.AddRange(notifications);
        await pmsContext.SaveChangesAsync();

        if (notifier != null && notifications.Count > 0)
        {
            var unread = await pmsContext.UserNotifications
                .Where(n => n.ReadAt == null && recipients.Contains(n.UserId))
                .GroupBy(n => n.UserId)
                .Select(g => new { UserId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.UserId, g => g.Count);

            foreach (var notification in notifications)
            {
                try
                {
                    await notifier.NotificationCreatedAsync(notification.UserId, NotificationService.ToModel(notification), unread.GetValueOrDefault(notification.UserId));
                }
                catch (Exception ex)
                {
                    // Already saved, so the customer sees it on the next read; one dead connection must not stop the others
                    logger?.LogWarning(ex, "Could not push price notification {NotificationId} to its owner", notification.Id);
                }
            }
        }

        return notifications.Count;
    }

    /// <summary>
    /// A walk-in took a customer's booked bay (BookedBayCase): tells the booking's customer what happened, never the
    /// walk-in's plate, and the lot's Staff what they must do or did (see StaffBookedBayAsync). One notification per
    /// case, type and person.
    /// </summary>
    public async Task<bool> BookedBayCaseAsync(long caseId, string notificationType)
    {
        var bayCase = await pmsContext.BookedBayCases.AsNoTracking().FirstOrDefaultAsync(c => c.Id == caseId);
        if (bayCase == null)
            return false;
        var reservation = await pmsContext.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == bayCase.ReservationId);
        if (reservation == null)
            return false;

        var lot = await pmsContext.ParkingLots.AsNoTracking().FirstOrDefaultAsync(l => l.Id == bayCase.ParkingLotId);
        var slotIds = new[] { bayCase.SlotId, bayCase.NewSlotId ?? 0 };
        var codes = await pmsContext.Slots.AsNoTracking().Where(s => slotIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Code);
        var bookedBay = codes.GetValueOrDefault(bayCase.SlotId) ?? "your bay";
        var newBay = bayCase.NewSlotId is { } id ? codes.GetValueOrDefault(id) : null;
        var lotName = lot?.Name ?? "the parking lot";
        var arrival = LocalTime(reservation.StartTime, lot);
        static string Money(decimal value) => $"{value.ToString("N0", CultureInfo.InvariantCulture)} VND";

        (string Title, string Message)? text = notificationType switch
        {
            NotificationTypes.BookedBayTaken => ("Your booked bay is taken",
                $"Another vehicle parked in bay {bookedBay} you booked at {lotName} for {arrival}. Staff will move your booking to another bay and let you know."),
            NotificationTypes.BookedBayReassigned => ("Your booking moved to another bay",
                $"Your booking {reservation.ReservationCode} at {lotName} now has bay {newBay ?? "a new bay"} instead of {bookedBay}. The first hour is free."),
            NotificationTypes.BookedBayFreed => ("Your booked bay is free again",
                $"The vehicle in bay {bookedBay} at {lotName} has left. Your booking {reservation.ReservationCode} keeps that bay."),
            NotificationTypes.BookingCancelledByLot => ("Your booking was cancelled by the lot",
                $"Bay {bookedBay} you booked at {lotName} was taken and no other bay of your vehicle type was free, so the lot cancelled booking {reservation.ReservationCode}. Sorry for the trouble."),
            NotificationTypes.DepositRefunded when bayCase.DepositRefunded > 0 => ("Deposit refunded to your XXIV wallet",
                $"{Money(bayCase.DepositRefunded)} deposit of booking {reservation.ReservationCode} is back in your XXIV wallet."),
            NotificationTypes.CompensationPaid when bayCase.CompensationPaid > 0 => ("Compensation paid to your XXIV wallet",
                $"{lotName} paid you {Money(bayCase.CompensationPaid)} because bay {bookedBay} you booked was taken. It is in your XXIV wallet."),
            _ => null
        };
        var toStaff = await StaffBookedBayAsync(bayCase, reservation, notificationType, lotName, bookedBay, newBay, arrival);
        toStaff |= await ManagerBookedBayAsync(bayCase, reservation, notificationType, lot, bookedBay, newBay, arrival);
        if (text == null || string.IsNullOrEmpty(reservation.UserId))
            return toStaff;

        var data = new Dictionary<string, string>
        {
            ["reservationId"] = reservation.Id.ToString(CultureInfo.InvariantCulture),
            ["reservationCode"] = reservation.ReservationCode,
            ["lotId"] = bayCase.ParkingLotId.ToString(CultureInfo.InvariantCulture),
            ["lotName"] = lot?.Name,
            ["bayCode"] = bookedBay,
            ["newBayCode"] = newBay,
            ["amount"] = notificationType switch
            {
                NotificationTypes.DepositRefunded => bayCase.DepositRefunded.ToString(CultureInfo.InvariantCulture),
                NotificationTypes.CompensationPaid => bayCase.CompensationPaid.ToString(CultureInfo.InvariantCulture),
                _ => null
            }
        };
        var toCustomer = await SaveAndPushAsync(new UserNotification
        {
            UserId = reservation.UserId,
            Type = notificationType,
            Title = text.Value.Title,
            Message = text.Value.Message,
            RefType = "BookedBayCase",
            RefId = bayCase.Id.ToString(CultureInfo.InvariantCulture),
            EventAt = bayCase.ResolvedAt ?? bayCase.CreatedAt
        }, data);
        return toCustomer || toStaff;
    }

    /// <summary>
    /// The lot's Staff bell for a taken booked bay: the bay to deal with (move the booking in Exceptions &amp; Alerts),
    /// the booking moved to its new bay, and the money sent to the customer's XXIV wallet (compensation and the
    /// refunded deposit, in one notice). The Staff who resolved the case reads "You sent…". Never the walk-in's plate.
    /// </summary>
    private async Task<bool> StaffBookedBayAsync(BookedBayCase bayCase, Reservation reservation, string notificationType,
        string lotName, string bookedBay, string newBay, string arrival)
    {
        static string Money(decimal value) => $"{value.ToString("N0", CultureInfo.InvariantCulture)} VND";
        // The money notice goes out once: with the compensation, or with the deposit when no compensation was paid
        var moneyNotice = (notificationType == NotificationTypes.CompensationPaid && bayCase.CompensationPaid > 0)
                          || (notificationType == NotificationTypes.DepositRefunded && bayCase.CompensationPaid <= 0 && bayCase.DepositRefunded > 0);
        if (notificationType is not (NotificationTypes.BookedBayTaken or NotificationTypes.BookedBayReassigned) && !moneyNotice)
            return false;

        var recipients = await pmsContext.Employees.AsNoTracking()
            .Where(e => e.ParkingLotId == bayCase.ParkingLotId && e.Status == "Active" && e.Position != "Manager" && e.UserId != null)
            .Select(e => e.UserId)
            .ToListAsync();
        if (!string.IsNullOrEmpty(bayCase.ResolvedBy) && notificationType != NotificationTypes.BookedBayTaken)
            recipients.Add(bayCase.ResolvedBy);
        recipients = recipients.Distinct().ToList();
        if (recipients.Count == 0)
            return false;

        var money = bayCase.CompensationPaid > 0 && bayCase.DepositRefunded > 0
            ? $"{Money(bayCase.CompensationPaid)} compensation and the {Money(bayCase.DepositRefunded)} deposit"
            : bayCase.CompensationPaid > 0 ? $"{Money(bayCase.CompensationPaid)} compensation" : $"the {Money(bayCase.DepositRefunded)} deposit";
        var data = new Dictionary<string, string>
        {
            ["audience"] = "staff",
            ["caseId"] = bayCase.Id.ToString(CultureInfo.InvariantCulture),
            ["reservationCode"] = reservation.ReservationCode,
            ["lotId"] = bayCase.ParkingLotId.ToString(CultureInfo.InvariantCulture),
            ["lotName"] = lotName,
            ["bayCode"] = bookedBay,
            ["newBayCode"] = newBay,
            ["amount"] = moneyNotice ? (bayCase.CompensationPaid + bayCase.DepositRefunded).ToString(CultureInfo.InvariantCulture) : null
        };

        var sent = false;
        foreach (var userId in recipients)
        {
            var resolver = userId == bayCase.ResolvedBy;
            (string Title, string Message) text = notificationType switch
            {
                NotificationTypes.BookedBayTaken => ("Booked bay taken: move the booking",
                    $"A walk-in parked in bay {bookedBay} at {lotName}, booked by booking {reservation.ReservationCode} for {arrival}. Move the booking to another free bay in Exceptions & Alerts, or compensate the customer when none is left."),
                NotificationTypes.BookedBayReassigned => (resolver ? "You moved a booking to another bay" : "A booking moved to another bay",
                    $"Booking {reservation.ReservationCode} at {lotName} now has bay {newBay ?? "a new bay"} instead of {bookedBay}. The customer was told; the first hour is free."),
                _ => (resolver ? "You sent compensation to the customer" : "Compensation sent to a customer",
                    $"{(resolver ? "You sent" : "The lot sent")} {money} to the XXIV wallet of the customer of booking {reservation.ReservationCode}, because bay {bookedBay} at {lotName} was taken. The customer was told.")
            };
            sent |= await SaveAndPushAsync(new UserNotification
            {
                UserId = userId,
                Type = notificationType,
                Title = text.Title,
                Message = text.Message,
                RefType = "BookedBayCase",
                RefId = bayCase.Id.ToString(CultureInfo.InvariantCulture),
                EventAt = bayCase.ResolvedAt ?? bayCase.CreatedAt
            }, data);
        }
        return sent;
    }

    /// <summary>
    /// Booking notices: the planned departure of a parked vehicle's scheduled booking is near (DepartureSoon) or has
    /// passed (DepartureOverdue, also to the lot's Staff), or the booking moved away from a bay still in use when its
    /// hold started (BookingBayMoved). Nothing is sent once the situation is over (the vehicle left, the booking ended).
    /// </summary>
    public async Task<bool> ReservationNoticeAsync(long reservationId, string notificationType, int? previousSlotId)
    {
        var reservation = await pmsContext.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reservationId);
        if (reservation == null || string.IsNullOrEmpty(reservation.UserId))
            return false;

        var lot = await pmsContext.ParkingLots.AsNoTracking().FirstOrDefaultAsync(l => l.Id == reservation.ParkingLotId);
        var lotName = lot?.Name ?? "the parking lot";
        var slotIds = new[] { reservation.SlotId ?? 0, previousSlotId ?? 0 };
        var codes = await pmsContext.Slots.AsNoTracking().Where(s => slotIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Code);
        var bay = reservation.SlotId is { } slotId ? codes.GetValueOrDefault(slotId) : null;
        var data = new Dictionary<string, string>
        {
            ["reservationId"] = reservation.Id.ToString(CultureInfo.InvariantCulture),
            ["reservationCode"] = reservation.ReservationCode,
            ["lotId"] = reservation.ParkingLotId.ToString(CultureInfo.InvariantCulture),
            ["lotName"] = lot?.Name,
            ["bayCode"] = bay
        };

        (string Title, string Message) text;
        string refId = reservation.Id.ToString(CultureInfo.InvariantCulture);
        switch (notificationType)
        {
            case NotificationTypes.DepartureSoon or NotificationTypes.DepartureOverdue:
            {
                // Only while the vehicle is still parked on this booking
                if (reservation.Status != ReservationStatus.CheckedIn || reservation.DepartureTime is not { } departureUtc)
                    return false;
                var departure = LocalTime(departureUtc, lot);
                data["departureTime"] = Iso(departureUtc);
                if (notificationType == NotificationTypes.DepartureSoon)
                {
                    text = ("Your booked time ends soon",
                        $"Your booking {reservation.ReservationCode} at {lotName} ends at {departure}. Please drive out by then: " +
                        "another driver may need the bay. If you stay longer, you pay the extra time by the lot's price list.");
                }
                else
                {
                    text = ("Your booked time has ended",
                        $"Your booking {reservation.ReservationCode} at {lotName} ended at {departure}, and your vehicle is still in " +
                        $"bay {bay ?? "your bay"}. The fee keeps running by the lot's price list; please drive out as soon as you can.");
                    await StaffOverstayAsync(reservation, lotName, bay, departure, data);
                }
                break;
            }
            case NotificationTypes.BookingBayMoved:
            {
                if (reservation.Status is not (ReservationStatus.Confirmed or ReservationStatus.PendingDeposit))
                    return false;
                var previousBay = previousSlotId is { } previous ? codes.GetValueOrDefault(previous) : null;
                data["previousBayCode"] = previousBay;
                // A booking can move more than once; each new bay is its own notice
                refId = $"{reservation.Id}:{reservation.SlotId}";
                text = ("Your booking moved to another bay",
                    $"Bay {previousBay ?? "you booked"} at {lotName} is still in use, so your booking {reservation.ReservationCode} " +
                    $"for {LocalTime(reservation.StartTime, lot)} now has bay {bay ?? "a new bay"} of the same type. Nothing else changes.");
                break;
            }
            default:
                return false;
        }

        return await SaveAndPushAsync(new UserNotification
        {
            UserId = reservation.UserId,
            Type = notificationType,
            Title = text.Title,
            Message = text.Message,
            RefType = "Reservation",
            RefId = refId,
            EventAt = Now
        }, data);
    }

    /// <summary>The lot's Staff bell for a vehicle still parked after its scheduled booking ended. Never the plate.</summary>
    private async Task StaffOverstayAsync(Reservation reservation, string lotName, string bay, string departure,
        Dictionary<string, string> customerData)
    {
        var recipients = await pmsContext.Employees.AsNoTracking()
            .Where(e => e.ParkingLotId == reservation.ParkingLotId && e.Status == "Active" && e.Position != "Manager" && e.UserId != null)
            .Select(e => e.UserId)
            .Distinct()
            .ToListAsync();
        var data = new Dictionary<string, string>(customerData) { ["audience"] = "staff" };
        foreach (var userId in recipients)
        {
            await SaveAndPushAsync(new UserNotification
            {
                UserId = userId,
                Type = NotificationTypes.DepartureOverdue,
                Title = "A booked vehicle stayed past its time",
                Message = $"The vehicle of booking {reservation.ReservationCode} is still in bay {bay ?? "its bay"} at {lotName}; " +
                          $"its booked time ended at {departure}. Bookings that need this bay later are moved to a free bay " +
                          "automatically; check the lot if none is free.",
                RefType = "Reservation",
                RefId = reservation.Id.ToString(CultureInfo.InvariantCulture),
                EventAt = Now
            }, data);
        }
    }

    /// <summary>
    /// The lot Manager's bell once a taken booked bay is handled: who moved the booking from which bay to which, or,
    /// when no bay was free, who compensated which booking and how much went to the customer's XXIV wallet.
    /// One notice per case and outcome (the compensation is told once, whichever of its events comes first).
    /// Never the walk-in's plate.
    /// </summary>
    private async Task<bool> ManagerBookedBayAsync(BookedBayCase bayCase, Reservation reservation, string notificationType,
        ParkingLot lot, string bookedBay, string newBay, string arrival)
    {
        // The walk-in was just found in the bay: told even if Staff already handled it before this job ran
        var taken = notificationType == NotificationTypes.BookedBayTaken;
        var moved = notificationType == NotificationTypes.BookedBayReassigned && bayCase.Status == BookedBayCaseStatus.Reassigned;
        var compensated = bayCase.Status == BookedBayCaseStatus.Compensated
                          && notificationType is NotificationTypes.BookingCancelledByLot or NotificationTypes.DepositRefunded or NotificationTypes.CompensationPaid;
        if (!taken && !moved && !compensated)
            return false;
        var managerId = await LotManagerAsync(bayCase.ParkingLotId);
        if (managerId == null)
            return false;

        static string Money(decimal value) => $"{value.ToString("N0", CultureInfo.InvariantCulture)} VND";
        var lotName = lot?.Name ?? "the parking lot";
        var at = LocalTime(bayCase.ResolvedAt ?? Now, lot);
        var bySystem = bayCase.ResolvedBy == BookedBayCaseService.SystemActor;
        var byManager = managerId == bayCase.ResolvedBy;
        var who = byManager ? "You" : bySystem ? "The system" : await EmployeeNameAsync(bayCase.ResolvedBy);
        var atLane = bayCase.ResolutionNote?.Contains("entry lane", StringComparison.OrdinalIgnoreCase) == true ? " at the entry lane" : "";
        var takenText = $"A walk-in parked in bay {bookedBay} at {lotName}, booked by booking {reservation.ReservationCode} for {arrival}.";

        (string Title, string Message) text;
        if (taken)
        {
            text = ("A walk-in took a booked bay",
                $"{takenText} Staff must move the booking to another free bay, or compensate the customer when none is left. " +
                "You will be told how it was handled.");
        }
        else if (moved)
        {
            text = (bySystem ? "A booking was moved to another bay" : $"{who} moved a booking to another bay",
                $"{takenText} {who} moved the booking from bay {bookedBay} to bay {newBay ?? "a new bay"}{atLane} at {at}. " +
                "The customer was told and gets the first hour free.");
        }
        else
        {
            var money = (bayCase.CompensationPaid, bayCase.DepositRefunded) switch
            {
                ( > 0, > 0) => $"{Money(bayCase.CompensationPaid)} compensation and the {Money(bayCase.DepositRefunded)} deposit back",
                ( > 0, _) => $"{Money(bayCase.CompensationPaid)} compensation",
                (_, > 0) => $"the {Money(bayCase.DepositRefunded)} deposit back (no compensation is set in the price list)",
                _ => null
            };
            var outcome = reservation.Status == ReservationStatus.LotCancelled ? "cancelled the booking" : "let the customer park as a walk-in";
            text = (bySystem ? "A customer was compensated" : $"{who} compensated a customer",
                $"{takenText} No other bay of that vehicle type was free, so {(byManager ? "you" : who)} {outcome}{atLane} at {at}" +
                (money == null
                    ? ". No money was due: no deposit was paid and no compensation is set in the price list."
                    : $" and sent {money} ({Money(bayCase.CompensationPaid + bayCase.DepositRefunded)} in all) to the customer's XXIV wallet."));
        }

        return await SaveAndPushAsync(new UserNotification
        {
            UserId = managerId,
            // The compensation has several events (cancelled, deposit, compensation): one Manager notice for all of them
            Type = taken ? NotificationTypes.BookedBayTaken : moved ? NotificationTypes.BookedBayReassigned : NotificationTypes.CompensationPaid,
            Title = text.Title,
            Message = text.Message,
            RefType = "BookedBayCase",
            RefId = bayCase.Id.ToString(CultureInfo.InvariantCulture),
            EventAt = taken ? bayCase.CreatedAt : bayCase.ResolvedAt ?? bayCase.CreatedAt
        }, new Dictionary<string, string>
        {
            ["audience"] = "manager",
            ["caseId"] = bayCase.Id.ToString(CultureInfo.InvariantCulture),
            ["reservationCode"] = reservation.ReservationCode,
            ["lotId"] = bayCase.ParkingLotId.ToString(CultureInfo.InvariantCulture),
            ["lotName"] = lot?.Name,
            ["bayCode"] = bookedBay,
            ["newBayCode"] = moved ? newBay : null,
            ["staffName"] = taken || bySystem || byManager ? null : who,
            ["compensation"] = compensated ? bayCase.CompensationPaid.ToString(CultureInfo.InvariantCulture) : null,
            ["depositRefunded"] = compensated ? bayCase.DepositRefunded.ToString(CultureInfo.InvariantCulture) : null
        });
    }

    /// <summary>
    /// A Staff closed their cash drawer: the lot Manager's bell says who, at what time, the count against the expected
    /// cash and the difference with its reason, so the Manager can confirm it in the Approval Queue.
    /// </summary>
    public async Task<bool> CashDrawerClosedAsync(long drawerId)
    {
        var drawer = await pmsContext.DailyCashCloses.AsNoTracking().FirstOrDefaultAsync(d => d.Id == drawerId);
        if (drawer?.ClosedAt == null)
            return false;
        var managerId = await LotManagerAsync(drawer.ParkingLotId);
        // The Manager never confirms their own drawer, so they get no notice about it either
        if (managerId == null || managerId == drawer.StaffUserId)
            return false;

        var lot = await pmsContext.ParkingLots.AsNoTracking().FirstOrDefaultAsync(l => l.Id == drawer.ParkingLotId);
        static string Money(decimal value) => $"{value.ToString("N0", CultureInfo.InvariantCulture)} VND";
        var who = await EmployeeNameAsync(drawer.StaffUserId);
        var counted = drawer.CountedTotal ?? 0;
        var expected = drawer.ExpectedTotal ?? 0;
        var difference = drawer.Difference ?? counted - expected;
        var result = difference == 0
            ? "It balances. Confirm it in the Approval Queue."
            : $"It is {Money(Math.Abs(difference))} {(difference > 0 ? "over" : "short")}" +
              (string.IsNullOrWhiteSpace(drawer.DifferenceReason) ? "." : $". Reason: \"{drawer.DifferenceReason.Trim()}\".") +
              " Check it in the Approval Queue.";

        return await SaveAndPushAsync(new UserNotification
        {
            UserId = managerId,
            Type = NotificationTypes.CashDrawerClosed,
            Title = difference == 0 ? $"{who} closed cash drawer {drawer.CloseNo}" : $"{who} closed cash drawer {drawer.CloseNo} with a difference",
            Message = $"{who} closed cash drawer {drawer.CloseNo} at {lot?.Name ?? "the parking lot"} at {LocalTime(drawer.ClosedAt.Value, lot)}. " +
                      $"Counted {Money(counted)}, expected {Money(expected)}. {result}",
            RefType = "DailyCashClose",
            RefId = drawer.Id.ToString(CultureInfo.InvariantCulture),
            EventAt = drawer.ClosedAt
        }, new Dictionary<string, string>
        {
            ["audience"] = "manager",
            ["drawerId"] = drawer.Id.ToString(CultureInfo.InvariantCulture),
            ["closeNo"] = drawer.CloseNo.ToString(CultureInfo.InvariantCulture),
            ["lotId"] = drawer.ParkingLotId.ToString(CultureInfo.InvariantCulture),
            ["lotName"] = lot?.Name,
            ["staffName"] = who,
            ["closedAt"] = Iso(drawer.ClosedAt.Value),
            ["countedTotal"] = counted.ToString(CultureInfo.InvariantCulture),
            ["expectedTotal"] = expected.ToString(CultureInfo.InvariantCulture),
            ["difference"] = difference.ToString(CultureInfo.InvariantCulture),
            ["reason"] = drawer.DifferenceReason
        });
    }

    /// <summary>The active Manager of the lot (ParkingLots.ManagerUserId), or null.</summary>
    private Task<string> LotManagerAsync(int lotId) =>
        pmsContext.ParkingLots.AsNoTracking()
            .Where(l => l.Id == lotId && !l.IsDeleted && l.ManagerUserId != null)
            .Where(l => pmsContext.Employees.Any(e => e.UserId == l.ManagerUserId && e.Status == "Active"))
            .Select(l => l.ManagerUserId)
            .FirstOrDefaultAsync();

    private async Task<string> EmployeeNameAsync(string userId) =>
        (string.IsNullOrEmpty(userId) ? null
            : await pmsContext.Employees.AsNoTracking().Where(e => e.UserId == userId).Select(e => e.FullName).FirstOrDefaultAsync())
        ?? "A Staff member";

    /// <summary>Daily: drops read notifications after 60 days and unread ones after 180. Returns the number deleted.</summary>
    public async Task<int> PurgeOldNotificationsAsync()
    {
        var readCutoff = Now.AddDays(-ReadRetentionDays);
        var unreadCutoff = Now.AddDays(-UnreadRetentionDays);
        var old = await pmsContext.UserNotifications
            .Where(n => (n.ReadAt != null && n.CreatedAt <= readCutoff) || n.CreatedAt <= unreadCutoff)
            .ToListAsync();
        pmsContext.UserNotifications.RemoveRange(old);
        await pmsContext.SaveChangesAsync();
        return old.Count;
    }

    private async Task<(ParkingSession Session, string UserId, ParkingLot Lot)> LoadSessionAsync(long sessionId)
    {
        var session = await pmsContext.ParkingSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session?.CustomerId == null)
            return (session, null, null);

        var userId = await pmsContext.Customers.Where(c => c.Id == session.CustomerId).Select(c => c.UserId).FirstOrDefaultAsync();
        var lot = await pmsContext.ParkingLots.AsNoTracking().FirstOrDefaultAsync(l => l.Id == session.ParkingLotId);
        return (session, string.IsNullOrEmpty(userId) ? null : userId, lot);
    }

    private static Dictionary<string, string> SessionData(ParkingSession session, ParkingLot lot) => new()
    {
        ["plateNumber"] = session.EntryPlateNumber,
        ["sessionCode"] = session.SessionCode,
        ["lotId"] = session.ParkingLotId.ToString(CultureInfo.InvariantCulture),
        ["lotName"] = lot?.Name,
        ["entryTime"] = Iso(session.EntryTime)
    };

    /// <summary>Writes the notification unless this event was already notified, then pushes it to the owner.</summary>
    private async Task<bool> SaveAndPushAsync(UserNotification notification, Dictionary<string, string> data)
    {
        // Hangfire retries a failed job: one event, one notification
        if (await pmsContext.UserNotifications.AnyAsync(n => n.Type == notification.Type && n.RefId == notification.RefId && n.UserId == notification.UserId))
            return false;

        notification.DataJson = JsonSerializer.Serialize(data.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value));
        notification.CreatedAt = Now;
        pmsContext.UserNotifications.Add(notification);
        await pmsContext.SaveChangesAsync();

        if (notifier != null)
        {
            var unread = await pmsContext.UserNotifications.CountAsync(n => n.UserId == notification.UserId && n.ReadAt == null);
            await notifier.NotificationCreatedAsync(notification.UserId, NotificationService.ToModel(notification), unread);
        }

        return true;
    }

    /// <summary>"10:32, 23/10/2026" in the lot's own time zone (Vietnam by default).</summary>
    private static string LocalTime(DateTime utc, ParkingLot lot)
    {
        // Falls back to Vietnam time (UTC+7) when the lot's zone is unknown on this server
        var zone = ParkingText.FindTimeZone(lot?.TimeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        return local.ToString("HH:mm, dd/MM/yyyy", CultureInfo.InvariantCulture);
    }

    private static string Duration(int minutes) =>
        minutes < 60 ? $"{minutes} min"
        : minutes % 60 == 0 ? $"{minutes / 60} h"
        : $"{minutes / 60} h {minutes % 60} min";

    private static string Iso(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
}
