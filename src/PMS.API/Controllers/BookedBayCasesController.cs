using PMS.API.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Operations;
using PMS.Application.Services.Impl;

namespace PMS.API.Controllers;

/// <summary>
/// Booked bays a walk-in (or an overstaying vehicle) took, on the Staff Exceptions screen: tow the vehicle to a penalty
/// bay, move the booking to another free bay, or, when none is left, cancel it with the deposit refunded and a
/// compensation paid into the customer's XXIV wallet. Staff
/// act on their assigned lot, Managers on the lots they manage.
/// </summary>
[Route("api/staff")]
[Authorize(Roles = "Staff,Manager,Admin")]
[RequireOnShift]
public class BookedBayCasesController(BookedBayCaseService bookedBays) : ApiController
{
    /// <summary>Cases of the lot, open ones first; <c>openOnly=false</c> adds the resolved ones.</summary>
    [HttpGet("lots/{lotId:int}/booked-bay-cases")]
    public async Task<IActionResult> GetAsync(int lotId, [FromQuery] bool openOnly = false) =>
        Ok(ApiResult<IReadOnlyList<BookedBayCaseModel>>.Success(await bookedBays.GetForLotAsync(lotId, openOnly)));

    /// <summary>
    /// Tows the offending vehicle to a free penalty bay (or a free walk-in bay when every penalty bay is taken): the
    /// booking keeps its bay and the vehicle owes the penalty of its pricing rule at check-out.
    /// </summary>
    [HttpPost("booked-bay-cases/{id:long}/tow")]
    public async Task<IActionResult> TowAsync(long id, [FromBody] TowVehicleModel model) =>
        Ok(ApiResult<BookedBayCaseModel>.Success(await bookedBays.TowAsync(id, model)));

    /// <summary>Moves the booking to a free bay of its vehicle type; the customer is told and gets the first hour free.</summary>
    [HttpPost("booked-bay-cases/{id:long}/reassign")]
    public async Task<IActionResult> ReassignAsync(long id, [FromBody] ReassignBookedBayModel model) =>
        Ok(ApiResult<BookedBayCaseModel>.Success(await bookedBays.ReassignAsync(id, model)));

    /// <summary>No bay of the vehicle type is free: cancels the booking, refunds the deposit and pays the compensation to the wallet.</summary>
    [HttpPost("booked-bay-cases/{id:long}/compensate")]
    public async Task<IActionResult> CompensateAsync(long id, [FromBody] CompensateBookedBayModel model) =>
        Ok(ApiResult<BookedBayCaseModel>.Success(await bookedBays.CompensateAsync(id, model)));
}
