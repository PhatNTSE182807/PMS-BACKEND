using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Reports;
using PMS.Application.Services;

namespace PMS.API.Controllers;

/// <summary>
/// Revenue reporting endpoints for Admins covering system-wide metrics and individual active lot filtering.
/// </summary>
[Route("api/admin/reports/revenue")]
[Authorize(Roles = "Admin")]
public class AdminRevenueController(
    IRevenueReportService revenueReportService) : ApiController
{
    [HttpGet]
    public async Task<IActionResult> GetRevenueAsync(
        [FromQuery] string startDate = null,
        [FromQuery] string endDate = null,
        [FromQuery] int? parkingLotId = null)
    {
        var result = await revenueReportService.GetAdminRevenueAsync(
            startDate, endDate, parkingLotId);

        return Ok(ApiResult<AdminRevenueReportResponse>.Success(result));
    }
}
