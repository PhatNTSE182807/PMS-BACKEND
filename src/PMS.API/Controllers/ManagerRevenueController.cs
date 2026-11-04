using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Reports;
using PMS.Application.Services;
using PMS.Shared.Services;

namespace PMS.API.Controllers;

/// <summary>
/// Revenue reporting endpoints scoped strictly to the authenticated Manager's assigned parking lots.
/// </summary>
[Route("api/manager/reports/revenue")]
[Authorize(Roles = "Manager")]
public class ManagerRevenueController(
    IRevenueReportService revenueReportService,
    IClaimService claimService) : ApiController
{
    [HttpGet]
    public async Task<IActionResult> GetRevenueAsync(
        [FromQuery] string startDate = null,
        [FromQuery] string endDate = null,
        [FromQuery] int? parkingLotId = null)
    {
        var result = await revenueReportService.GetManagerRevenueAsync(
            claimService.GetUserId(), startDate, endDate, parkingLotId);

        return Ok(ApiResult<ManagerRevenueReportResponse>.Success(result));
    }

    /// <summary>Total revenue since the first parking session of the Manager's lots (or of one lot), split by payment method.</summary>
    [HttpGet("all-time")]
    public async Task<IActionResult> GetAllTimeRevenueAsync([FromQuery] int? parkingLotId = null) =>
        Ok(ApiResult<ManagerRevenueReportResponse>.Success(
            await revenueReportService.GetManagerAllTimeRevenueAsync(claimService.GetUserId(), parkingLotId)));
}
