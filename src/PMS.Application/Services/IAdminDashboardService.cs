using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PMS.Application.Models.Dashboard;

namespace PMS.Application.Services;

/// <summary>
/// Network-wide numbers for the Admin dashboard: headline totals and the performance of every parking lot.
/// Revenue is the FinalFee of settled sessions, the same rule as the revenue report.
/// </summary>
public interface IAdminDashboardService
{
    Task<DashboardOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default);

    /// <summary>Every parking lot that is not deleted, best month-to-date revenue first.</summary>
    Task<List<HubPerformanceDto>> GetHubsAsync(CancellationToken cancellationToken = default);
}
