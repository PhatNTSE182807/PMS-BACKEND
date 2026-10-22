using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Models.VehicleDocuments;
using PMS.Application.Services;
using PMS.DataAccess.Persistence;

namespace PMS.Application.Jobs;

/// <summary>
/// Background work on vehicle papers, run by Hangfire (see PMS.API): the daily deletion of photos
/// 30 days after the final decision, and of drafts abandoned for as long (BR-26).
/// </summary>
public class VehicleDocumentJobs(
    PmsDbContext pmsContext,
    IVehicleDocumentStorage storage,
    VehicleDocumentStorageOptions storageOptions)
{
    public const string PurgeDocumentsJobId = "purge-vehicle-documents";

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
}
