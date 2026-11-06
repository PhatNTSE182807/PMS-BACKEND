using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.Operations;
using PMS.Application.Services.Impl;

namespace PMS.API.Controllers;

/// <summary>
/// Booked bays a walk-in took (BR-08), on the Staff Exceptions screen: move the booking to another free bay, or, when
/// none is left, cancel it with the deposit refunded and a compensation paid into the customer's XXIV wallet. Staff
/// act on their assigned lot, Managers on the lots they manage.
/// </summary>
[Route("api/staff")]
[Authorize(Roles = "Staff,Manager,Admin")]
public class BookedBayCasesController(BookedBayCaseService bookedBays) : ApiController
{
    /// <summary>Cases of the lot, open ones first; <c>openOnly=false</c> adds the resolved ones.</summary>
    [HttpGet("lots/{lotId:int}/booked-bay-cases")]
    public async Task<IActionResult> GetAsync(int lotId, [FromQuery] bool openOnly = false) =>
        Ok(ApiResult<IReadOnlyList<BookedBayCaseModel>>.Success(await bookedBays.GetForLotAsync(lotId, openOnly)));

    /// <summary>Moves the booking to a free bay of its vehicle type; the customer is told and gets the first hour free.</summary>
    [HttpPost("booked-bay-cases/{id:long}/reassign")]
    public async Task<IActionResult> ReassignAsync(long id, [FromBody] ReassignBookedBayModel model) =>
        Ok(ApiResult<BookedBayCaseModel>.Success(await bookedBays.ReassignAsync(id, model)));

    /// <summary>No bay of the vehicle type is free: cancels the booking, refunds the deposit and pays the compensation to the wallet.</summary>
    [HttpPost("booked-bay-cases/{id:long}/compensate")]
    public async Task<IActionResult> CompensateAsync(long id, [FromBody] CompensateBookedBayModel model) =>
        Ok(ApiResult<BookedBayCaseModel>.Success(await bookedBays.CompensateAsync(id, model)));
}
