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
}
