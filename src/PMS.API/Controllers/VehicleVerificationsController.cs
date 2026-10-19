using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.VehicleDocuments;
using PMS.Application.Services;
using PMS.Shared.Services;

namespace PMS.API.Controllers;

/// <summary>
/// Manual review of vehicle paper checks that OCR could not verify (SRS FR-VER-03/04): mismatches,
/// unreadable or low-confidence fields. Reserved for the "Xác minh phương tiện" (vehicle verification)
/// permission, which Staff hold; Managers and Admins can act too.
/// </summary>
[Route("api/vehicle-verifications")]
[Tags("Vehicle Verification")]
[Authorize(Roles = "Staff,Manager,Admin")]
public class VehicleVerificationsController(
    IVehicleDocumentService vehicleDocumentService,
    IClaimService claimService) : ApiController
{
    /// <summary>Checks with the given status (default NeedsReview), oldest first.</summary>
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
