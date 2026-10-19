using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PMS.Application.Models.VehicleDocuments;

namespace PMS.Application.Services;

/// <summary>
/// Upload of a vehicle's papers (cà vẹt front/back, CCCD front/back), OCR verification in the background
/// and the Staff review of checks that need a person (SRS FR-VER-01..04, BR-26).
/// </summary>
public interface IVehicleDocumentService
{
    /// <summary>Adds or replaces one file of the vehicle's draft check. Only the vehicle's owner may upload.</summary>
    Task<VehicleVerificationModel> UploadAsync(string userId, Guid vehicleId, string documentType, string side,
        Stream content, string fileName, long length);

    /// <summary>Submits the draft (all four sides uploaded) and queues the OCR job.</summary>
    Task<VehicleVerificationModel> SubmitAsync(string userId, Guid vehicleId);

    /// <summary>The vehicle's latest check, or null when its papers were never uploaded.</summary>
    Task<VehicleVerificationModel> GetLatestAsync(string userId, Guid vehicleId);

    /// <summary>One of the owner's own files (for previews).</summary>
    Task<VehicleDocumentContent> OpenFileAsync(string userId, Guid vehicleId, Guid documentId);

    /// <summary>Checks waiting for Staff (default NeedsReview), oldest first.</summary>
    Task<List<VehicleVerificationReviewModel>> GetReviewQueueAsync(string status);

    Task<VehicleVerificationReviewModel> GetForReviewAsync(Guid verificationId);

    Task<VehicleDocumentContent> OpenFileForReviewAsync(Guid verificationId, Guid documentId);

    /// <summary>Approves or rejects a check in NeedsReview.</summary>
    Task<VehicleVerificationReviewModel> ReviewAsync(string staffUserId, Guid verificationId, ReviewVehicleVerificationModel model);
}

/// <summary>Queues the OCR of a submitted check (Hangfire in the API).</summary>
public interface IVehicleVerificationQueue
{
    void Enqueue(Guid verificationId);
}

/// <summary>Pushes check status changes to the uploader and the Staff review queue (SignalR in the API).</summary>
public interface IVehicleVerificationNotifier
{
    Task VerificationChangedAsync(string userId, Guid verificationId, Guid vehicleId, string status);
}
