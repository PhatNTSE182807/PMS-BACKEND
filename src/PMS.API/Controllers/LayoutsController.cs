using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PMS.Application.Models;
using PMS.Application.Models.Layout;
using PMS.Application.Services;

namespace PMS.API.Controllers;

/// <summary>
/// Layout 3D of a lot and its 2D fallback (SRS FR-3D). Layouts come from the ready-made model the Admin
/// picked for the lot; the public channel returns geometry and slot statuses, never plates (FR-3D-08).
/// </summary>
[Route("api/public/lots/{lotId:int}/layout")]
[AllowAnonymous]
[EnableRateLimiting(ApiDependencyInjection.PublicLayoutRateLimit)]
public class LayoutsController(ILayoutService layoutService) : ApiController
{
    /// <summary>The ready-made parking buildings (3D models) an Admin picks from when registering a lot.</summary>
    [HttpGet("/api/lot-templates")]
    public IActionResult GetTemplates()
    {
        return Ok(ApiResult<IReadOnlyList<LotTemplateResponseModel>>.Success(layoutService.GetTemplates()));
    }

    /// <summary>Published 3D layout: floors, zones and slot cells. Changes only with the version, so cache it.</summary>
    [HttpGet]
    public async Task<IActionResult> GetPublishedAsync(int lotId)
    {
        return Ok(ApiResult<LayoutResponseModel>.Success(await layoutService.GetPublishedAsync(lotId)));
    }

    /// <summary>Live slot statuses and free spaces per zone, to colour the 3D model (FR-3D-02).</summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatusAsync(int lotId)
    {
        return Ok(ApiResult<LayoutStatusResponseModel>.Success(await layoutService.GetStatusAsync(lotId)));
    }

    /// <summary>2D fallback when WebGL 2 is missing or slow (FR-3D-06); detail = slots (default) or zones.</summary>
    [HttpGet("2d")]
    public async Task<IActionResult> Get2DAsync(int lotId, [FromQuery] string detail = LayoutDetail.Slots)
    {
        return Ok(ApiResult<Layout2DResponseModel>.Success(await layoutService.Get2DAsync(lotId, detail)));
    }
}
