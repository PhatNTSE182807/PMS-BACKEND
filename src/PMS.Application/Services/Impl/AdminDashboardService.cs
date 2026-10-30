using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Dashboard;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Reads the Admin dashboard straight from the database. Every number comes from a fixed number of
/// set-based queries (see <see cref="AdminDashboardQueries"/>), never from a query per lot, so the cost does
/// not grow with the number of lots: the overview runs 4 statements and the hub list 4.
/// <para>
/// Revenue is the FinalFee of settled sessions, as in <see cref="RevenueReportService"/>. "Today" and
/// "month to date" use the default parking time zone.
/// </para>
/// </summary>
public class AdminDashboardService(
    PmsDbContext pmsContext,
    UserManager<ApplicationUser> userManager,
    TimeProvider timeProvider) : IAdminDashboardService
{
    private const string CustomerRole = "User";
    private const string ManagerRole = "Manager";
    private const string StaffRole = "Staff";

    public async Task<DashboardOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        // 2 statements: bays per lot (one row per lot each), added up here
        var bays = await BaysByLotAsync(cancellationToken);

        // 1 statement: all-time settled revenue
        var totalRevenue = await pmsContext.SettledSessionsOfLiveLots().AsNoTracking()
            .SumAsync(s => s.FinalFee, cancellationToken);

        // 1 statement: active accounts per role
        var accounts = await userManager.Users
            .Where(u => u.IsActive && (u.Role == CustomerRole || u.Role == ManagerRole || u.Role == StaffRole))
            .GroupBy(u => u.Role)
            .Select(g => new { Role = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var totalBays = bays.Sum(b => b.Total);
        var occupiedBays = bays.Sum(b => b.Occupied);

        return new DashboardOverviewDto
        {
            TotalGrossRevenue = totalRevenue,
            NetworkLoadFactor = Percent(occupiedBays, totalBays),
            TotalBays = totalBays,
            OccupiedBays = occupiedBays,
            ActiveUsers = accounts.Where(a => a.Role == CustomerRole).Sum(a => a.Count),
            ActiveStaff = accounts.Where(a => a.Role is ManagerRole or StaffRole).Sum(a => a.Count)
        };
    }

    public async Task<List<HubPerformanceDto>> GetHubsAsync(CancellationToken cancellationToken = default)
    {
        var (todayStartUtc, tomorrowStartUtc, monthStartUtc) = CurrentPeriodsUtc();

        // 1 statement: the lots
        var lots = await pmsContext.ParkingLots.AsNoTracking()
            .Where(l => !l.IsDeleted)
            .Select(l => new { l.Id, l.Name, l.Address, l.District, l.City })
            .ToListAsync(cancellationToken);

        // 2 statements: bays per lot
        var bays = (await BaysByLotAsync(cancellationToken)).ToDictionary(b => b.LotId);

        // 1 statement: today's and month-to-date revenue per lot
        var revenue = (await pmsContext.RevenueByLot(monthStartUtc, todayStartUtc, tomorrowStartUtc).AsNoTracking()
                .ToListAsync(cancellationToken))
            .ToDictionary(r => r.LotId);

        return lots
            .Select(l =>
            {
                bays.TryGetValue(l.Id, out var lotBays);
                revenue.TryGetValue(l.Id, out var lotRevenue);
                var total = lotBays?.Total ?? 0;
                var occupied = lotBays?.Occupied ?? 0;

                return new HubPerformanceDto
                {
                    LotId = l.Id,
                    LotName = l.Name,
                    Location = string.Join(", ", new[] { l.Address, l.District, l.City }.Where(p => !string.IsNullOrWhiteSpace(p))),
                    TotalBays = total,
                    OccupiedBays = occupied,
                    OccupancyRate = Percent(occupied, total),
                    TodayRevenue = lotRevenue?.Today ?? 0m,
                    MTDRevenue = lotRevenue?.MonthToDate ?? 0m
                };
            })
            .OrderByDescending(h => h.MTDRevenue)
            .ThenBy(h => h.LotName)
            .ToList();
    }

    /// <summary>
    /// Bays of every lot: the zone counters, plus the slots of any zone that has no counter. The two grouped
    /// statements return one row per lot each; they are merged here.
    /// </summary>
    private async Task<List<LotBaysRow>> BaysByLotAsync(CancellationToken cancellationToken)
    {
        var fromCounters = await pmsContext.BaysFromCounters().AsNoTracking().ToListAsync(cancellationToken);
        var fromSlots = await pmsContext.BaysFromSlotsWithoutCounter().AsNoTracking().ToListAsync(cancellationToken);

        return fromCounters.Concat(fromSlots)
            .GroupBy(b => b.LotId)
            .Select(g => new LotBaysRow { LotId = g.Key, Total = g.Sum(b => b.Total), Occupied = g.Sum(b => b.Occupied) })
            .ToList();
    }

    /// <summary>Start of today, start of tomorrow and start of this month in the parking time zone, as UTC.</summary>
    private (DateTime TodayStart, DateTime TomorrowStart, DateTime MonthStart) CurrentPeriodsUtc()
    {
        var tz = ParkingText.FindTimeZone(null);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(timeProvider.GetUtcNow().UtcDateTime, tz);

        DateTime ToUtc(DateTime local) =>
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);

        var today = localNow.Date;
        return (ToUtc(today), ToUtc(today.AddDays(1)), ToUtc(new DateTime(today.Year, today.Month, 1)));
    }

    private static double Percent(int part, int whole) =>
        whole <= 0 ? 0 : Math.Round(part * 100d / whole, 1);
}
