using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Reads the customer's finished sessions: completed exits, emergency exits and sessions a Manager
/// closed. Open sessions stay on the "Active Session" screen and are not part of the history.
/// </summary>
public class ParkingHistoryService(PmsDbContext pmsContext, TimeProvider clock) : IParkingHistoryService
{
    /// <summary>The vehicle has left the lot.</summary>
    public static readonly string[] Finished = [SessionStatus.Completed, SessionStatus.EmergencyExit, SessionStatus.AbnormalClosed];

    private const int MaxPageSize = 50;

    public async Task<ParkingHistoryPageModel> GetHistoryAsync(string userId, ParkingHistoryQuery query)
    {
        query ??= new ParkingHistoryQuery();
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        if (query.From != null && query.To != null && query.From >= query.To)
            throw new BadRequestException("'from' must be before 'to'.");

        // No customer profile yet means no sessions yet
        var customerId = await CustomerIdAsync(userId);
        if (customerId == null)
            return new ParkingHistoryPageModel { Page = page, PageSize = pageSize };

        var sessions = pmsContext.ParkingSessions.AsNoTracking()
            .Where(s => s.CustomerId == customerId && Finished.Contains(s.Status));
        if (query.VehicleId != null)
            sessions = sessions.Where(s => s.VehicleId == query.VehicleId);
        // A session without an exit time (closed by a Manager) counts from its entry
        if (query.From != null)
            sessions = sessions.Where(s => (s.ExitTime ?? s.EntryTime) >= query.From.Value);
        if (query.To != null)
            sessions = sessions.Where(s => (s.ExitTime ?? s.EntryTime) < query.To.Value);

        var totalCount = await sessions.CountAsync();
        var totalSpent = totalCount == 0 ? 0 : await sessions.SumAsync(s => s.FinalFee);
        var rows = await sessions
            .OrderByDescending(s => s.ExitTime ?? s.EntryTime)
            .ThenByDescending(s => s.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new ParkingHistoryPageModel
        {
            Items = await ToItemsAsync<ParkingHistoryItemModel>(rows),
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalSpent = totalSpent
        };
    }

    public async Task<ParkingReceiptModel> GetReceiptAsync(string userId, long sessionId)
    {
        var customerId = await CustomerIdAsync(userId);
        // Someone else's session looks exactly like a missing one
        var session = customerId == null
                          ? null
                          : await pmsContext.ParkingSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId && s.CustomerId == customerId)
                      ?? throw new NotFoundException("Parking session not found.");
        if (!Finished.Contains(session.Status))
            throw new BadRequestException("The receipt is available once the vehicle has left the lot.");

        var receipt = (await ToItemsAsync<ParkingReceiptModel>([session]))[0];
        var lot = await pmsContext.ParkingLots.AsNoTracking().FirstOrDefaultAsync(l => l.Id == session.ParkingLotId);

        receipt.CustomerName = await pmsContext.Customers.AsNoTracking().Where(c => c.Id == customerId).Select(c => c.FullName).FirstOrDefaultAsync();
        receipt.LotPhone = lot?.Phone;
        receipt.OriginalFee = session.OriginalFee;
        receipt.DiscountAmount = session.DiscountAmount;
        receipt.ZeroFeeReason = session.ZeroFeeReason;
        receipt.Payments = await PaymentsAsync(session);
        receipt.IssuedAt = clock.GetUtcNow().UtcDateTime;
        return receipt;
    }

    /// <summary>The deposit of the booking (when there was one) and every settled fee payment of the session.</summary>
    private async Task<List<ParkingReceiptPaymentModel>> PaymentsAsync(ParkingSession session)
    {
        var payments = await pmsContext.Payments.AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Succeeded
                        && ((p.SessionId == session.Id && p.Purpose == PaymentPurpose.ParkingFee)
                            || (session.ReservationId != null && p.ReservationId == session.ReservationId && p.Purpose == PaymentPurpose.Deposit)))
            .OrderBy(p => p.PaidAt ?? p.CreatedAt)
            .ToListAsync();

        return payments.Select(p => new ParkingReceiptPaymentModel
        {
            PaymentCode = p.PaymentCode,
            Purpose = p.Purpose,
            Method = p.PaymentMethod,
            Provider = p.GatewayProvider,
            Amount = p.Amount,
            PaidAt = p.PaidAt is { } paid ? Utc(paid) : null
        }).ToList();
    }

    /// <summary>Maps sessions with their lot, zone, slot, vehicle type, booking and invoice in a few queries.</summary>
    private async Task<List<T>> ToItemsAsync<T>(IReadOnlyCollection<ParkingSession> sessions) where T : ParkingHistoryItemModel, new()
    {
        if (sessions.Count == 0)
            return [];

        var ids = sessions.Select(s => s.Id).ToList();
        var lotIds = sessions.Select(s => s.ParkingLotId).Distinct().ToList();
        var zoneIds = sessions.Where(s => s.ZoneId != null).Select(s => s.ZoneId!.Value).Distinct().ToList();
        var slotIds = sessions.Where(s => s.SlotId != null).Select(s => s.SlotId!.Value).Distinct().ToList();
        var reservationIds = sessions.Where(s => s.ReservationId != null).Select(s => s.ReservationId!.Value).Distinct().ToList();

        var lots = await pmsContext.ParkingLots.AsNoTracking().Where(l => lotIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id);
        var zones = await pmsContext.Zones.AsNoTracking().Where(z => zoneIds.Contains(z.Id)).ToDictionaryAsync(z => z.Id, z => z.Name);
        var slots = await pmsContext.Slots.AsNoTracking().Where(s => slotIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Code);
        var types = await pmsContext.VehicleTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name);
        var bookings = await pmsContext.Reservations.AsNoTracking().Where(r => reservationIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.ReservationCode);
        // The latest paid invoice of each session is its receipt
        var invoices = (await pmsContext.Invoices.AsNoTracking()
                .Where(i => i.SessionId != null && ids.Contains(i.SessionId.Value) && i.Status == "Paid")
                .Select(i => new { SessionId = i.SessionId!.Value, i.InvoiceNumber, i.CreatedAt })
                .ToListAsync())
            .GroupBy(i => i.SessionId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.CreatedAt).First().InvoiceNumber);

        return sessions.Select(s =>
        {
            var lot = lots.GetValueOrDefault(s.ParkingLotId);
            var end = s.ExitTime ?? s.EntryTime;
            return new T
            {
                SessionId = s.Id,
                SessionCode = s.SessionCode,
                Status = s.Status,
                SessionType = s.SessionType,
                EntryTime = Utc(s.EntryTime),
                ExitTime = s.ExitTime is { } exit ? Utc(exit) : null,
                DurationMinutes = s.TotalDurationMinutes ?? Math.Max(0, (int)Math.Round((end - s.EntryTime).TotalMinutes)),
                ParkingLotId = s.ParkingLotId,
                LotName = lot?.Name,
                LotDistrict = lot?.District,
                LotAddress = lot?.Address,
                ZoneName = s.ZoneId != null ? zones.GetValueOrDefault(s.ZoneId.Value) : null,
                SlotCode = s.SlotId != null ? slots.GetValueOrDefault(s.SlotId.Value) : null,
                VehicleId = s.VehicleId,
                PlateNumber = s.ExitPlateNumber ?? s.EntryPlateNumber,
                VehicleTypeName = s.VehicleTypeId != null ? types.GetValueOrDefault(s.VehicleTypeId.Value) : null,
                ReservationCode = s.ReservationId != null ? bookings.GetValueOrDefault(s.ReservationId.Value) : null,
                FinalFee = s.FinalFee,
                DepositApplied = s.DepositApplied,
                PaidAmount = s.PaidAmount,
                ReceiptNumber = invoices.GetValueOrDefault(s.Id) ?? s.SessionCode
            };
        }).ToList();
    }

    private Task<int?> CustomerIdAsync(string userId) =>
        pmsContext.Customers.Where(c => c.UserId == userId).Select(c => (int?)c.Id).FirstOrDefaultAsync();

    /// <summary>Times are stored as UTC without a kind; mark them so JSON carries the "Z".</summary>
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
