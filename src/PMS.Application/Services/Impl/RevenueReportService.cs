using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using PMS.Application.Models.Reports;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Aggregates parking revenue directly from settled ParkingSession entities (Option A).
/// Adheres to role-separated scoping:
/// - Manager API: Scoped exclusively to active lots where ParkingLot.ManagerUserId == currentUserId.
/// - Admin API: Scoped to all active lots across the system, with optional single-lot filter.
/// Revenue definition: ParkingSession.FinalFee of Paid/Completed sessions.
/// </summary>
public class RevenueReportService(PmsDbContext pmsContext) : IRevenueReportService
{
    public async Task<ManagerRevenueReportResponse> GetManagerRevenueAsync(
        string managerUserId, string startDate, string endDate, int? parkingLotId = null)
    {
        if (string.IsNullOrWhiteSpace(managerUserId))
            throw new ForbiddenException("Authenticated user ID is required.");

        var managedLots = await pmsContext.ParkingLots.AsNoTracking()
            .Where(p => p.ManagerUserId == managerUserId && !p.IsDeleted)
            .OrderBy(p => p.Name)
            .ToListAsync();

        if (parkingLotId.HasValue)
        {
            var targetLot = managedLots.FirstOrDefault(p => p.Id == parkingLotId.Value);
            if (targetLot == null)
                throw new ForbiddenException("You do not manage this parking lot.");

            return await AggregateRevenueForLotsAsync(
                [targetLot],
                startDate,
                endDate,
                (sDate, eDate, totalRev, totalCount, byLot, methods) => new ManagerRevenueReportResponse
                {
                    ManagerUserId = managerUserId,
                    StartDate = sDate,
                    EndDate = eDate,
                    TotalRevenue = totalRev,
                    TotalSessions = totalCount,
                    ByLot = byLot,
                    PaymentMethods = methods
                });
        }

        if (managedLots.Count == 0)
        {
            var tz = ParkingText.FindTimeZone(null);
            var (sDate, eDate, _, _) = ResolveDateInterval(startDate, endDate, tz);
            return new ManagerRevenueReportResponse
            {
                ManagerUserId = managerUserId,
                StartDate = sDate.ToString("yyyy-MM-dd"),
                EndDate = eDate.ToString("yyyy-MM-dd"),
                TotalRevenue = 0m,
                TotalSessions = 0,
                ByLot = [],
                PaymentMethods = []
            };
        }

        return await AggregateRevenueForLotsAsync(
            managedLots,
            startDate,
            endDate,
            (sDate, eDate, totalRev, totalCount, byLot, methods) => new ManagerRevenueReportResponse
            {
                ManagerUserId = managerUserId,
                StartDate = sDate,
                EndDate = eDate,
                TotalRevenue = totalRev,
                TotalSessions = totalCount,
                ByLot = byLot,
                PaymentMethods = methods
            });
    }

    public async Task<AdminRevenueReportResponse> GetAdminRevenueAsync(
        string startDate, string endDate, int? parkingLotId = null)
    {
        List<ParkingLot> targetLots;
        if (parkingLotId.HasValue)
        {
            var lot = await pmsContext.ParkingLots.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == parkingLotId.Value && !p.IsDeleted)
                ?? throw new NotFoundException("Parking lot not found.");
            targetLots = [lot];
        }
        else
        {
            targetLots = await pmsContext.ParkingLots.AsNoTracking()
                .Where(p => !p.IsDeleted)
                .OrderBy(p => p.Name)
                .ToListAsync();
        }

        return await AggregateRevenueForLotsAsync(
            targetLots,
            startDate,
            endDate,
            (sDate, eDate, totalRev, totalCount, byLot, methods) => new AdminRevenueReportResponse
            {
                StartDate = sDate,
                EndDate = eDate,
                SystemTotalRevenue = totalRev,
                SystemTotalSessions = totalCount,
                ActiveLotsCount = targetLots.Count,
                ByLot = byLot,
                PaymentMethods = methods
            });
    }

    private async Task<TResponse> AggregateRevenueForLotsAsync<TResponse>(
        List<ParkingLot> targetLots,
        string startDateStr,
        string endDateStr,
        Func<string, string, decimal, int, IReadOnlyList<ParkingLotRevenueModel>, IReadOnlyList<PaymentMethodBreakdownModel>, TResponse> responseFactory)
    {
        var tz = targetLots.Count == 1
            ? ParkingText.FindTimeZone(targetLots[0].TimeZoneId)
            : ParkingText.FindTimeZone(null);

        var (sDate, eDate, startUtc, endUtc) = ResolveDateInterval(startDateStr, endDateStr, tz);

        var targetLotIds = targetLots.Select(l => l.Id).ToList();

        // Session eligibility rule:
        // - Only Completed (vehicle checked out) or Paid (session fee settled and paid, in exit window) sessions.
        // - Unpaid or in-progress states (PendingEntry, Active, PendingPayment, LostTicketPending, Cancelled) are excluded.
        // Deterministic reporting timestamp rule:
        // - Completed session: ExitTime is the deterministic timestamp when the session was closed at barrier.
        // - Paid session: Paid at kiosk/app, ExitTime is null; UpdatedAt records the exact timestamp when status became Paid.
        var eligibleSessions = await pmsContext.ParkingSessions.AsNoTracking()
            .Where(s => targetLotIds.Contains(s.ParkingLotId))
            .Where(s => (s.Status == SessionStatus.Completed && s.ExitTime >= startUtc && s.ExitTime < endUtc)
                     || (s.Status == SessionStatus.Paid && s.ExitTime == null && s.UpdatedAt >= startUtc && s.UpdatedAt < endUtc))
            .ToListAsync();

        var totalRevenue = eligibleSessions.Sum(s => s.FinalFee);
        var totalSessions = eligibleSessions.Count;

        // By-Lot Breakdown
        var byLot = new List<ParkingLotRevenueModel>();
        foreach (var lot in targetLots)
        {
            var lotSessions = eligibleSessions.Where(s => s.ParkingLotId == lot.Id).ToList();
            byLot.Add(new ParkingLotRevenueModel
            {
                ParkingLotId = lot.Id,
                ParkingLotName = lot.Name,
                TotalRevenue = lotSessions.Sum(s => s.FinalFee),
                TotalSessions = lotSessions.Count,
                ManagerUserId = lot.ManagerUserId
            });
        }

        // Payment-Method Allocation:
        // Each session's FinalFee is composed of:
        // 1. session.PaidAmount -> Paid at checkout (Cash, Online, Wallet)
        // 2. session.DepositApplied -> Deducted from reservation deposit (Online, Wallet)
        // Invariant: FinalFee == DepositApplied + PaidAmount (or 0 for waived fees).
        var sessionIds = eligibleSessions.Select(s => s.Id).ToList();
        var reservationIds = eligibleSessions
            .Where(s => s.ReservationId != null && s.DepositApplied > 0)
            .Select(s => s.ReservationId.Value)
            .Distinct()
            .ToList();

        var sessionPayments = sessionIds.Count > 0
            ? await pmsContext.Payments.AsNoTracking()
                .Where(p => p.SessionId != null && sessionIds.Contains(p.SessionId.Value)
                         && p.Purpose == PaymentPurpose.ParkingFee
                         && p.Status == PaymentStatus.Succeeded)
                .ToListAsync()
            : [];

        var depositPayments = reservationIds.Count > 0
            ? await pmsContext.Payments.AsNoTracking()
                .Where(p => p.ReservationId != null && reservationIds.Contains(p.ReservationId.Value)
                         && p.Purpose == PaymentPurpose.Deposit
                         && p.Status == PaymentStatus.Succeeded)
                .ToListAsync()
            : [];

        var sessionPaymentsLookup = sessionPayments
            .GroupBy(p => p.SessionId.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var depositPaymentsLookup = depositPayments
            .GroupBy(p => p.ReservationId.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        decimal cashTotal = 0m;
        decimal qrTotal = 0m;
        decimal walletTotal = 0m;
        decimal otherTotal = 0m;

        void AddToMethod(string method, decimal amount)
        {
            if (amount <= 0) return;
            var mapped = MapPaymentMethod(method);
            switch (mapped)
            {
                case "CASH": cashTotal += amount; break;
                case "QR_BANKING": qrTotal += amount; break;
                case "RFID_WALLET": walletTotal += amount; break;
                default: otherTotal += amount; break;
            }
        }

        foreach (var session in eligibleSessions)
        {
            if (session.FinalFee <= 0) continue;

            // 1. Allocate session.PaidAmount to checkout payment method(s)
            if (session.PaidAmount > 0)
            {
                if (sessionPaymentsLookup.TryGetValue(session.Id, out var payments) && payments.Count > 0)
                {
                    if (payments.Count == 1)
                    {
                        AddToMethod(payments[0].PaymentMethod, session.PaidAmount);
                    }
                    else
                    {
                        var paymentsSum = payments.Sum(p => p.Amount);
                        if (paymentsSum > 0)
                        {
                            foreach (var p in payments)
                            {
                                var portion = session.PaidAmount * (p.Amount / paymentsSum);
                                AddToMethod(p.PaymentMethod, portion);
                            }
                        }
                        else
                        {
                            AddToMethod(payments[0].PaymentMethod, session.PaidAmount);
                        }
                    }
                }
                else
                {
                    AddToMethod("OTHER", session.PaidAmount);
                }
            }

            // 2. Allocate session.DepositApplied to reservation deposit payment method(s)
            if (session.DepositApplied > 0)
            {
                if (session.ReservationId.HasValue && depositPaymentsLookup.TryGetValue(session.ReservationId.Value, out var depPayments) && depPayments.Count > 0)
                {
                    if (depPayments.Count == 1)
                    {
                        AddToMethod(depPayments[0].PaymentMethod, session.DepositApplied);
                    }
                    else
                    {
                        var depSum = depPayments.Sum(p => p.Amount);
                        if (depSum > 0)
                        {
                            foreach (var dp in depPayments)
                            {
                                var portion = session.DepositApplied * (dp.Amount / depSum);
                                AddToMethod(dp.PaymentMethod, portion);
                            }
                        }
                        else
                        {
                            AddToMethod(depPayments[0].PaymentMethod, session.DepositApplied);
                        }
                    }
                }
                else
                {
                    AddToMethod("OTHER", session.DepositApplied);
                }
            }

            // 3. Any unaccounted discrepancy between FinalFee and (DepositApplied + PaidAmount)
            var unaccounted = session.FinalFee - (session.DepositApplied + session.PaidAmount);
            if (unaccounted > 0)
            {
                AddToMethod("OTHER", unaccounted);
            }
        }

        var methodList = new List<PaymentMethodBreakdownModel>
        {
            new()
            {
                Method = "QR_BANKING",
                Amount = qrTotal,
                Percentage = totalRevenue > 0 ? Math.Round((qrTotal / totalRevenue) * 100m, 1) : 0m
            },
            new()
            {
                Method = "RFID_WALLET",
                Amount = walletTotal,
                Percentage = totalRevenue > 0 ? Math.Round((walletTotal / totalRevenue) * 100m, 1) : 0m
            },
            new()
            {
                Method = "CASH",
                Amount = cashTotal,
                Percentage = totalRevenue > 0 ? Math.Round((cashTotal / totalRevenue) * 100m, 1) : 0m
            }
        };

        if (otherTotal > 0)
        {
            methodList.Add(new()
            {
                Method = "OTHER",
                Amount = otherTotal,
                Percentage = totalRevenue > 0 ? Math.Round((otherTotal / totalRevenue) * 100m, 1) : 0m
            });
        }

        return responseFactory(
            sDate.ToString("yyyy-MM-dd"),
            eDate.ToString("yyyy-MM-dd"),
            totalRevenue,
            totalSessions,
            byLot,
            methodList);
    }

    private static (DateOnly StartDate, DateOnly EndDate, DateTime StartUtc, DateTime EndUtc) ResolveDateInterval(
        string startDateStr, string endDateStr, TimeZoneInfo tz)
    {
        DateOnly start;
        DateOnly end;

        if (string.IsNullOrWhiteSpace(startDateStr) && string.IsNullOrWhiteSpace(endDateStr))
        {
            var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            start = DateOnly.FromDateTime(localNow);
            end = start;
        }
        else if (string.IsNullOrWhiteSpace(startDateStr) || string.IsNullOrWhiteSpace(endDateStr))
        {
            throw new BadRequestException("Both 'startDate' and 'endDate' must be provided, or both omitted.");
        }
        else
        {
            if (!DateOnly.TryParseExact(startDateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out start))
                throw new BadRequestException("Invalid 'startDate' format. Expected yyyy-MM-dd.");

            if (!DateOnly.TryParseExact(endDateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out end))
                throw new BadRequestException("Invalid 'endDate' format. Expected yyyy-MM-dd.");
        }

        if (end < start)
            throw new BadRequestException("'endDate' cannot be before 'startDate'.");

        var startLocal = start.ToDateTime(TimeOnly.MinValue);
        var endLocal = end.AddDays(1).ToDateTime(TimeOnly.MinValue); // Half-open interval [start, end + 1 day)

        var startUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified), tz);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified), tz);

        return (start, end, startUtc, endUtc);
    }

    private static string MapPaymentMethod(string method) => method switch
    {
        PaymentMethod.Cash or "CASH" => "CASH",
        PaymentMethod.Online or "QR_BANKING" => "QR_BANKING",
        PaymentMethod.Wallet or "RFID_WALLET" => "RFID_WALLET",
        _ => "OTHER"
    };
}
