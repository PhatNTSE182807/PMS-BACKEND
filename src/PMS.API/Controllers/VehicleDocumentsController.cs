using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PMS.Application.Models;
using PMS.Application.Models.VehicleDocuments;
using PMS.Application.Services;
using PMS.Shared.Services;

namespace PMS.API.Controllers;

/// <summary>
/// The signed-in owner's vehicle papers: upload the front of the registration certificate (cà vẹt) and both
/// sides of the Citizen ID card (CCCD) (JPEG, PNG or PDF up to 10 MB each), submit them to Staff for review,
/// and follow the decision. Only the vehicle's owner can reach these endpoints; another user's vehicle answers 404.
/// </summary>
[Route("api/users/profile/vehicles/{vehicleId:guid}/documents")]
[Tags(SwaggerTags.Profile)]
[Authorize]
public class VehicleDocumentsController(
    IVehicleDocumentService vehicleDocumentService,
    IClaimService claimService) : ApiController
{
    /// <summary>A little above 10 MB so the service, not the server, reports a too-large file clearly.</summary>
    private const long UploadRequestLimit = 11 * 1024 * 1024;

    /// <summary>The vehicle's latest check (null when no papers were uploaded yet).</summary>
    [HttpGet]
    public async Task<IActionResult> GetLatestAsync(Guid vehicleId)
    {
        return Ok(ApiResult<VehicleVerificationModel>.Success(
            await vehicleDocumentService.GetLatestAsync(claimService.GetUserId(), vehicleId)));
    }

    /// <summary>Uploads or replaces one file: RegistrationCertificate/Front, CitizenId/Front or CitizenId/Back.</summary>
    [HttpPost("{documentType}/{side}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(UploadRequestLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadRequestLimit)]
    public async Task<IActionResult> UploadAsync(Guid vehicleId, string documentType, string side, IFormFile file)
    {
        if (file == null)
            return BadRequest(ApiResult<VehicleVerificationModel>.Failure(["Choose a file to upload."]));

        await using var stream = file.OpenReadStream();
        return Ok(ApiResult<VehicleVerificationModel>.Success(await vehicleDocumentService.UploadAsync(
            claimService.GetUserId(), vehicleId, documentType, side, stream, file.FileName, file.Length)));
    }

    /// <summary>Submits the uploaded papers to the Staff review queue; the decision is pushed over SignalR.</summary>
    [HttpPost("submit")]
    public async Task<IActionResult> SubmitAsync(Guid vehicleId)
    {
        return Ok(ApiResult<VehicleVerificationModel>.Success(
            await vehicleDocumentService.SubmitAsync(claimService.GetUserId(), vehicleId)));
    }

    /// <summary>One of the owner's own uploaded files, for previews.</summary>
    [HttpGet("files/{documentId:guid}")]
    public async Task<IActionResult> GetFileAsync(Guid vehicleId, Guid documentId)
    {
        var file = await vehicleDocumentService.OpenFileAsync(claimService.GetUserId(), vehicleId, documentId);
        Response.Headers.CacheControl = "private, no-store";
        return File(file.Content, file.ContentType, file.FileName);
    }
}
