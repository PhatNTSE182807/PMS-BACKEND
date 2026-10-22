using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.VehicleDocuments;
using PMS.Application.Services;
using PMS.Shared.Services;

namespace PMS.API.Controllers;

/// <summary>
/// Staff review of new vehicle registrations (SRS FR-VER-03/04): compare the uploaded registration
/// certificate and Citizen ID card with the vehicle and the account, then approve or reject the vehicle.
/// Staff hold this permission; Managers and Admins can act too.
/// </summary>
[Route("api/vehicle-verifications")]
[Tags("Vehicle Verification")]
[Authorize(Roles = "Staff,Manager,Admin")]
public class VehicleVerificationsController(
    IVehicleDocumentService vehicleDocumentService,
    IClaimService claimService) : ApiController
{
    /// <summary>Checks with the given status: NeedsReview (default, oldest first), Approved or Rejected (newest first).</summary>
    [HttpGet]
    public async Task<IActionResult> GetQueueAsync([FromQuery] string status = null)
    {
        return Ok(ApiResult<List<VehicleVerificationReviewModel>>.Success(await vehicleDocumentService.GetReviewQueueAsync(status)));
    }

    [HttpGet("{verificationId:guid}")]
    public async Task<IActionResult> GetAsync(Guid verificationId)
    {
        return Ok(ApiResult<VehicleVerificationReviewModel>.Success(await vehicleDocumentService.GetForReviewAsync(verificationId)));
    }

    [HttpGet("{verificationId:guid}/files/{documentId:guid}")]
    public async Task<IActionResult> GetFileAsync(Guid verificationId, Guid documentId)
    {
        var file = await vehicleDocumentService.OpenFileForReviewAsync(verificationId, documentId);
        Response.Headers.CacheControl = "private, no-store";
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>Approves or rejects a check waiting for review; a rejection needs a note for the customer.</summary>
    [HttpPost("{verificationId:guid}/review")]
    public async Task<IActionResult> ReviewAsync(Guid verificationId, [FromBody] ReviewVehicleVerificationModel model)
    {
        return Ok(ApiResult<VehicleVerificationReviewModel>.Success(
            await vehicleDocumentService.ReviewAsync(claimService.GetUserId(), verificationId, model)));
    }
}
