using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Helpers.VehicleDocuments;
using PMS.Application.Models.VehicleDocuments;
using PMS.Application.Services;
using PMS.Application.Services.Ocr;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Jobs;

/// <summary>
/// Background work on vehicle papers, run by Hangfire (see PMS.API): OCR and verification of a submitted
/// check (FR-VER-02/03), and the daily deletion of photos 30 days after the final decision (BR-26).
/// </summary>
public class VehicleDocumentJobs(
    PmsDbContext pmsContext,
    IVehicleDocumentStorage storage,
    IDocumentOcrService ocr,
    OcrOptions ocrOptions,
    VehicleDocumentStorageOptions storageOptions,
    IVehicleVerificationNotifier notifier = null)
{
    public const string PurgeDocumentsJobId = "purge-vehicle-documents";

    /// <summary>Reads both papers, compares them with the vehicle and the profile, and stores the outcome.</summary>
    public async Task ProcessAsync(Guid verificationId)
    {
        var verification = await pmsContext.VehicleVerifications
            .Include(v => v.Documents)
            .Include(v => v.Vehicle).ThenInclude(v => v.Customer)
            .FirstOrDefaultAsync(v => v.Id == verificationId);

        // Already processed (a retried job) or withdrawn: nothing to do
        if (verification == null || verification.Status is not (VehicleVerificationStatus.Pending or VehicleVerificationStatus.Processing))
            return;

        verification.Status = VehicleVerificationStatus.Processing;
        await pmsContext.SaveChangesAsync();
        await NotifyAsync(verification);

        var certificateLines = new List<OcrLine>();
        var cardLines = new List<OcrLine>();
        string engineError = null;

        // Front first, then back, so labelled values are found where they are printed
        foreach (var document in verification.Documents.OrderBy(d => d.Side == VehicleDocumentKinds.Front ? 0 : 1))
        {
            await using var stream = storage.OpenRead(document.StoragePath);
            if (stream == null)
                continue;

            var result = await ocr.ReadAsync(stream, document.ContentType);
            if (!result.Succeeded)
            {
                engineError ??= result.Error;
                continue;
            }

            (document.DocumentType == VehicleDocumentKinds.RegistrationCertificate ? certificateLines : cardLines).AddRange(result.Lines);
        }

        var vehicle = verification.Vehicle;
        var customer = vehicle.Customer;
        var outcome = VehicleDocumentVerifier.Verify(
            RegistrationCertificateParser.Parse(certificateLines),
            CitizenIdCardParser.Parse(cardLines),
            new VerificationExpectations
            {
                Plate = vehicle.PlateNumber,
                Brand = vehicle.Brand,
                Model = vehicle.Model,
                Color = vehicle.Color,
                ProfileFullName = customer?.FullName,
                ProfileCitizenId = customer?.CitizenId,
                ProfileDateOfBirth = customer?.DateOfBirth,
                OwnedBySomeoneElse = !string.IsNullOrEmpty(vehicle.CitizenId) && vehicle.CitizenId != customer?.CitizenId
            },
            new VerificationThresholds { AutoVerify = ocrOptions.AutoVerifyConfidence, Unreadable = ocrOptions.UnreadableConfidence });

        var now = DateTime.UtcNow;
        verification.ResultJson = JsonSerializer.Serialize(outcome.Fields);
        verification.Confidence = (decimal)Math.Round(outcome.Confidence, 4);
        verification.ProcessedAt = now;

        if (engineError != null)
        {
            // The engine failed, not the customer: a person checks the papers instead
            verification.Status = VehicleVerificationStatus.NeedsReview;
            verification.StatusReason = "Automatic reading is unavailable right now; Staff will check your papers.";
        }
        else
        {
            verification.Status = outcome.Status;
            verification.StatusReason = outcome.Reason;
            if (outcome.Status is VehicleVerificationStatus.AutoVerified or VehicleVerificationStatus.Rejected)
                verification.DecidedAt = now;
        }

        await pmsContext.SaveChangesAsync();
        await NotifyAsync(verification);
    }

    /// <summary>
    /// BR-26: deletes the photos of checks decided more than RetentionDays ago (the result row stays),
    /// and drafts abandoned for as long. Returns the number of files deleted.
    /// </summary>
    public async Task<int> PurgeExpiredDocumentsAsync()
    {
        var cutoff = DateTime.UtcNow.AddDays(-storageOptions.RetentionDays);
        var decided = await pmsContext.VehicleVerifications
            .Include(v => v.Documents)
            .Where(v => v.FilesDeletedAt == null && v.DecidedAt != null && v.DecidedAt <= cutoff)
            .ToListAsync();
        var abandoned = await pmsContext.VehicleVerifications
            .Include(v => v.Documents)
            .Where(v => v.Status == VehicleVerificationStatus.Draft && v.CreatedAt <= cutoff)
            .ToListAsync();

        var deleted = 0;
        foreach (var document in decided.Concat(abandoned).SelectMany(v => v.Documents).Where(d => d.StoragePath != null))
        {
            storage.Delete(document.StoragePath);
            document.StoragePath = null;
            deleted++;
        }

        foreach (var verification in decided)
            verification.FilesDeletedAt = DateTime.UtcNow;

        pmsContext.VehicleVerifications.RemoveRange(abandoned);
        await pmsContext.SaveChangesAsync();
        return deleted;
    }

    private Task NotifyAsync(VehicleVerification verification) =>
        notifier?.VerificationChangedAsync(verification.UserId, verification.Id, verification.VehicleId, verification.Status) ?? Task.CompletedTask;
}
