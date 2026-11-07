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
                    TotalCompensation = byLot.Sum(l => l.Compensation),
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
                TotalCompensation = byLot.Sum(l => l.Compensation),
                ByLot = byLot,
                PaymentMethods = methods
            });
    }

    public async Task<ManagerRevenueReportResponse> GetManagerAllTimeRevenueAsync(string managerUserId, int? parkingLotId = null)
    {
        if (string.IsNullOrWhiteSpace(managerUserId))
            throw new ForbiddenException("Authenticated user ID is required.");

        var lotIds = await pmsContext.ParkingLots.AsNoTracking()
            .Where(p => p.ManagerUserId == managerUserId && !p.IsDeleted)
            .Select(p => p.Id)
            .ToListAsync();
        if (parkingLotId.HasValue && !lotIds.Contains(parkingLotId.Value))
            throw new ForbiddenException("You do not manage this parking lot.");
        if (parkingLotId.HasValue)
            lotIds = [parkingLotId.Value];

        // From the day of the first session of these lots (a day early, so no time zone cuts it off) to today
        var tz = ParkingText.FindTimeZone(null);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));
        var firstEntry = await pmsContext.ParkingSessions.AsNoTracking()
            .Where(s => lotIds.Contains(s.ParkingLotId))
            .MinAsync(s => (DateTime?)s.EntryTime);
        var start = firstEntry is { } first ? DateOnly.FromDateTime(first).AddDays(-1) : today;
        if (start > today) start = today;

        return await GetManagerRevenueAsync(managerUserId, start.ToString("yyyy-MM-dd"), today.AddDays(1).ToString("yyyy-MM-dd"), parkingLotId);
    }

    public async Task<AdminRevenueReportResponse> GetAdminRevenueAsync(
        string startDate, string endDate, int? parkingLotId = null, string vehicleType = null)
    {
        var kind = ParseVehicleType(vehicleType);
        var targetLots = await ResolveAdminLotsAsync(parkingLotId);
        var kinds = await LoadVehicleKindsAsync();
        IReadOnlyList<VehicleKindRevenueModel> byVehicleType = [];

        var response = await AggregateRevenueForLotsAsync(
            targetLots,
            startDate,
            endDate,
            (sDate, eDate, totalRev, totalCount, byLot, methods) => new AdminRevenueReportResponse
            {
                StartDate = sDate,
                EndDate = eDate,
                SystemTotalRevenue = totalRev,
                SystemTotalSessions = totalCount,
                SystemTotalCompensation = byLot.Sum(l => l.Compensation),
                ActiveLotsCount = targetLots.Count,
                ByLot = byLot,
                PaymentMethods = methods
            },
            s => kind == null || KindOf(s, kinds) == kind,
            sessions => byVehicleType = SplitByVehicleKind(sessions, kinds),
            vehicleTypeId => kind == null || KindOfType(vehicleTypeId, kinds) == kind);

        response.ByVehicleType = byVehicleType;
        return response;
    }

    public async Task<AdminRevenueTimeSeriesResponse> GetAdminRevenueTimeSeriesAsync(
        string startDate, string endDate, int? parkingLotId = null, string vehicleType = null, string granularity = null)
    {
        var kind = ParseVehicleType(vehicleType);
        var step = ParseGranularity(granularity);
        var targetLots = await ResolveAdminLotsAsync(parkingLotId);
        var tz = targetLots.Count == 1 ? ParkingText.FindTimeZone(targetLots[0].TimeZoneId) : ParkingText.FindTimeZone(null);
        var (sDate, eDate, startUtc, endUtc) = ResolveDateInterval(startDate, endDate, tz);
        step ??= AutoGranularity(sDate, eDate);

        var kinds = await LoadVehicleKindsAsync();
        var lotNames = targetLots.ToDictionary(l => l.Id, l => l.Name);
        var sessions = (await LoadEligibleSessionsAsync(lotNames.Keys.ToList(), startUtc, endUtc))
            .Where(s => kind == null || KindOf(s, kinds) == kind)
            .ToList();

        var periods = SplitRange(sDate, eDate, step);
        var buckets = sessions
            .GroupBy(s => PeriodIndex(periods, ReportedLocalDate(s, tz)))
            .ToDictionary(g => g.Key, g => g.ToList());
        // Compensations come off the period they were paid in, and off their vehicle family
        var paidOut = (await LoadCompensationsAsync(lotNames.Keys.ToList(), startUtc, endUtc))
            .Where(c => kind == null || KindOfType(c.VehicleTypeId, kinds) == kind)
            .GroupBy(c => PeriodIndex(periods, DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(c.PaidAt, DateTimeKind.Utc), tz))))
            .ToDictionary(g => g.Key, g => g.ToList());

        var points = periods.Select((period, i) =>
        {
            var inPeriod = buckets.TryGetValue(i, out var list) ? list : [];
            var compensated = paidOut.TryGetValue(i, out var paid) ? paid : [];
            var cars = inPeriod.Where(s => KindOf(s, kinds) == CarKind).ToList();
            var motorbikes = inPeriod.Where(s => KindOf(s, kinds) == MotorbikeKind).ToList();
            decimal CompensationOf(string family) => compensated.Where(c => KindOfType(c.VehicleTypeId, kinds) == family).Sum(c => c.Amount);
            var lotIds = inPeriod.Select(s => s.ParkingLotId).Concat(compensated.Select(c => c.LotId)).Distinct();
            return new RevenueTimeSeriesPointModel
            {
                StartDate = period.Start.ToString("yyyy-MM-dd"),
                EndDate = period.End.ToString("yyyy-MM-dd"),
                TotalRevenue = inPeriod.Sum(s => s.FinalFee) - compensated.Sum(c => c.Amount),
                Compensation = compensated.Sum(c => c.Amount),
                TotalSessions = inPeriod.Count,
                CarRevenue = cars.Sum(s => s.FinalFee) - CompensationOf(CarKind),
                CarSessions = cars.Count,
                MotorbikeRevenue = motorbikes.Sum(s => s.FinalFee) - CompensationOf(MotorbikeKind),
                MotorbikeSessions = motorbikes.Count,
                ByLot = lotIds
                    .Select(lotId =>
                    {
                        var lotSessions = inPeriod.Where(s => s.ParkingLotId == lotId).ToList();
                        var lotCompensation = compensated.Where(c => c.LotId == lotId).Sum(c => c.Amount);
                        return new RevenueTimeSeriesLotModel
                        {
                            ParkingLotId = lotId,
                            ParkingLotName = lotNames[lotId],
                            TotalRevenue = lotSessions.Sum(s => s.FinalFee) - lotCompensation,
                            Compensation = lotCompensation,
                            TotalSessions = lotSessions.Count,
                            CarSessions = lotSessions.Count(s => KindOf(s, kinds) == CarKind),
                            MotorbikeSessions = lotSessions.Count(s => KindOf(s, kinds) == MotorbikeKind)
                        };
                    })
                    .OrderBy(l => l.ParkingLotName)
                    .ToList()
            };
        }).ToList();

        return new AdminRevenueTimeSeriesResponse
        {
            StartDate = sDate.ToString("yyyy-MM-dd"),
            EndDate = eDate.ToString("yyyy-MM-dd"),
            Granularity = step,
            Points = points
        };
    }

    private async Task<TResponse> AggregateRevenueForLotsAsync<TResponse>(
        List<ParkingLot> targetLots,
        string startDateStr,
        string endDateStr,
        Func<string, string, decimal, int, IReadOnlyList<ParkingLotRevenueModel>, IReadOnlyList<PaymentMethodBreakdownModel>, TResponse> responseFactory,
        Func<ParkingSession, bool> sessionFilter = null,
        Action<IReadOnlyList<ParkingSession>> onSessions = null,
        Func<int?, bool> compensationFilter = null)
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
        var eligibleSessions = await LoadEligibleSessionsAsync(targetLotIds, startUtc, endUtc);
        if (sessionFilter != null)
            eligibleSessions = eligibleSessions.Where(sessionFilter).ToList();
        onSessions?.Invoke(eligibleSessions);

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

        // Compensations the lots paid in the period come off their revenue (the methods above stay what was collected)
        var compensations = (await LoadCompensationsAsync(targetLotIds, startUtc, endUtc))
            .Where(c => compensationFilter == null || compensationFilter(c.VehicleTypeId))
            .ToList();
        foreach (var lot in byLot)
        {
            lot.Compensation = compensations.Where(c => c.LotId == lot.ParkingLotId).Sum(c => c.Amount);
            lot.TotalRevenue -= lot.Compensation;
        }

        return responseFactory(
            sDate.ToString("yyyy-MM-dd"),
            eDate.ToString("yyyy-MM-dd"),
            totalRevenue - compensations.Sum(c => c.Amount),
            totalSessions,
            byLot,
            methodList);
    }

    /// <summary>A compensation a lot paid into a customer's wallet (a walk-in took their booked bay).</summary>
    private sealed record CompensationRow(int LotId, DateTime PaidAt, decimal Amount, int? VehicleTypeId);

    /// <summary>Compensations paid by the lots in [start, end), with the booked vehicle type.</summary>
    private async Task<List<CompensationRow>> LoadCompensationsAsync(IReadOnlyCollection<int> lotIds, DateTime startUtc, DateTime endUtc)
    {
        var rows = await pmsContext.Payments.AsNoTracking()
            .Where(p => p.Purpose == PaymentPurpose.Compensation && p.Status == PaymentStatus.Succeeded
                        && p.ParkingLotId != null && lotIds.Contains(p.ParkingLotId.Value)
                        && p.PaidAt >= startUtc && p.PaidAt < endUtc)
            .Select(p => new { LotId = p.ParkingLotId!.Value, PaidAt = p.PaidAt!.Value, p.Amount, p.ReservationId })
            .ToListAsync();
        var reservationIds = rows.Where(r => r.ReservationId != null).Select(r => r.ReservationId!.Value).Distinct().ToList();
        var types = await pmsContext.Reservations.AsNoTracking()
            .Where(r => reservationIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.VehicleTypeId);
        return rows.Select(r => new CompensationRow(r.LotId, r.PaidAt, r.Amount, r.ReservationId is { } id ? types.GetValueOrDefault(id) : null)).ToList();
    }

    private static string KindOfType(int? vehicleTypeId, IReadOnlyDictionary<int, string> kinds) =>
        vehicleTypeId is { } id && kinds.TryGetValue(id, out var kind) ? kind : OtherKind;

    private const string CarKind = "CAR";
    private const string MotorbikeKind = "MOTORBIKE";
    private const string OtherKind = "OTHER";

    /// <summary>Settled sessions of the lots reported in [startUtc, endUtc): Completed by exit time, Paid (not yet out) by payment time.</summary>
    private Task<List<ParkingSession>> LoadEligibleSessionsAsync(List<int> lotIds, DateTime startUtc, DateTime endUtc) =>
        pmsContext.ParkingSessions.AsNoTracking()
            .Where(s => lotIds.Contains(s.ParkingLotId))
            .Where(s => (s.Status == SessionStatus.Completed && s.ExitTime >= startUtc && s.ExitTime < endUtc)
                     || (s.Status == SessionStatus.Paid && s.ExitTime == null && s.UpdatedAt >= startUtc && s.UpdatedAt < endUtc))
            .ToListAsync();

    private async Task<List<ParkingLot>> ResolveAdminLotsAsync(int? parkingLotId)
    {
        if (parkingLotId.HasValue)
        {
            var lot = await pmsContext.ParkingLots.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == parkingLotId.Value && !p.IsDeleted)
                ?? throw new NotFoundException("Parking lot not found.");
            return [lot];
        }

        return await pmsContext.ParkingLots.AsNoTracking()
            .Where(p => !p.IsDeleted)
            .OrderBy(p => p.Name)
            .ToListAsync();
    }

    /// <summary>Vehicle type id to family: CAR_4 and CAR_7 are cars, MOTORBIKE is motorbikes, anything else OTHER.</summary>
    private async Task<Dictionary<int, string>> LoadVehicleKindsAsync() =>
        (await pmsContext.VehicleTypes.AsNoTracking().Select(t => new { t.Id, t.Code }).ToListAsync())
            .ToDictionary(t => t.Id, t => KindOfCode(t.Code));

    private static string KindOfCode(string code)
    {
        var upper = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (upper == MotorbikeKind) return MotorbikeKind;
        return upper.StartsWith(CarKind, StringComparison.Ordinal) ? CarKind : OtherKind;
    }

    private static string KindOf(ParkingSession session, Dictionary<int, string> kinds) =>
        session.VehicleTypeId is { } id && kinds.TryGetValue(id, out var kind) ? kind : OtherKind;

    private static string ParseVehicleType(string vehicleType)
    {
        var value = (vehicleType ?? string.Empty).Trim().ToUpperInvariant();
        return value switch
        {
            "" or "ALL" => null,
            CarKind or MotorbikeKind => value,
            _ => throw new BadRequestException("Invalid 'vehicleType'. Use ALL, CAR or MOTORBIKE.")
        };
    }

    private static List<VehicleKindRevenueModel> SplitByVehicleKind(IReadOnlyList<ParkingSession> sessions, Dictionary<int, string> kinds)
    {
        var groups = sessions.GroupBy(s => KindOf(s, kinds)).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<VehicleKindRevenueModel>();
        foreach (var kind in new[] { CarKind, MotorbikeKind, OtherKind })
        {
            var list = groups.TryGetValue(kind, out var found) ? found : [];
            if (kind == OtherKind && list.Count == 0) continue;
            result.Add(new VehicleKindRevenueModel { VehicleType = kind, TotalRevenue = list.Sum(s => s.FinalFee), TotalSessions = list.Count });
        }
        return result;
    }

    private static string ParseGranularity(string granularity)
    {
        var value = (granularity ?? string.Empty).Trim().ToUpperInvariant();
        return value switch
        {
            "" or "AUTO" => null,
            "DAY" or "WEEK" or "MONTH" or "YEAR" => value,
            _ => throw new BadRequestException("Invalid 'granularity'. Use DAY, WEEK, MONTH or YEAR.")
        };
    }

    /// <summary>The finest step that keeps about a month of points: days up to 31 days, weeks up to 16 weeks, months up to 24 months.</summary>
    private static string AutoGranularity(DateOnly start, DateOnly end)
    {
        var days = end.DayNumber - start.DayNumber + 1;
        if (days <= 31) return "DAY";
        if (days <= 16 * 7) return "WEEK";
        var months = (end.Year - start.Year) * 12 + end.Month - start.Month + 1;
        return months <= 24 ? "MONTH" : "YEAR";
    }

    /// <summary>Calendar days, Monday-to-Sunday weeks, months or years covering the range; the first and last are clipped to it.</summary>
    private static List<(DateOnly Start, DateOnly End)> SplitRange(DateOnly start, DateOnly end, string step)
    {
        var periods = new List<(DateOnly, DateOnly)>();
        for (var cursor = start; cursor <= end;)
        {
            var next = step switch
            {
                "WEEK" => cursor.AddDays(7 - ((int)cursor.DayOfWeek + 6) % 7),
                "MONTH" => new DateOnly(cursor.Year, cursor.Month, 1).AddMonths(1),
                "YEAR" => new DateOnly(cursor.Year + 1, 1, 1),
                _ => cursor.AddDays(1)
            };
            var last = next.AddDays(-1);
            periods.Add((cursor, last < end ? last : end));
            cursor = next;
        }
        return periods;
    }

    private static int PeriodIndex(List<(DateOnly Start, DateOnly End)> periods, DateOnly date)
    {
        for (var i = 0; i < periods.Count; i++)
            if (date >= periods[i].Start && date <= periods[i].End) return i;
        // The query already keeps sessions inside the range; clamp anything on its edge.
        return date < periods[0].Start ? 0 : periods.Count - 1;
    }

    /// <summary>Local calendar day a session counts on: exit time when Completed, payment time when Paid and not yet out.</summary>
    private static DateOnly ReportedLocalDate(ParkingSession session, TimeZoneInfo tz)
    {
        var utc = session.Status == SessionStatus.Completed && session.ExitTime.HasValue
            ? session.ExitTime.Value
            : session.UpdatedAt ?? session.EntryTime;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz));
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
