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
        [FromQuery] int? parkingLotId = null,
        [FromQuery] string vehicleType = null)
    {
        var result = await revenueReportService.GetAdminRevenueAsync(
            startDate, endDate, parkingLotId, vehicleType);

        return Ok(ApiResult<AdminRevenueReportResponse>.Success(result));
    }

    /// <summary>Settled revenue per day, week, month or year of the range, split into cars and motorbikes and by lot.</summary>
    [HttpGet("timeseries")]
    public async Task<IActionResult> GetTimeSeriesAsync(
        [FromQuery] string startDate = null,
        [FromQuery] string endDate = null,
        [FromQuery] int? parkingLotId = null,
        [FromQuery] string vehicleType = null,
        [FromQuery] string granularity = null) =>
        Ok(ApiResult<AdminRevenueTimeSeriesResponse>.Success(
            await revenueReportService.GetAdminRevenueTimeSeriesAsync(startDate, endDate, parkingLotId, vehicleType, granularity)));
}
