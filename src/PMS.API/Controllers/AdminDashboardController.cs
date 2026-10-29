using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Dashboard;
using PMS.Application.Services;

namespace PMS.API.Controllers;

/// <summary>
/// Network overview and per-lot performance for the Admin dashboard. Admin only: the numbers cover every lot,
/// while a Manager only acts inside the lots assigned to them.
/// </summary>
[Route("api/admin/dashboard")]
[Authorize(Roles = "Admin")]
public class AdminDashboardController(IAdminDashboardService dashboardService) : ApiController
{
    [HttpGet("overview")]
    public async Task<IActionResult> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var overview = await dashboardService.GetOverviewAsync(cancellationToken);
        return Ok(ApiResult<DashboardOverviewDto>.Success(overview));
    }

    [HttpGet("hubs")]
    public async Task<IActionResult> GetHubsAsync(CancellationToken cancellationToken)
    {
        var hubs = await dashboardService.GetHubsAsync(cancellationToken);
        return Ok(ApiResult<List<HubPerformanceDto>>.Success(hubs));
    }
}
