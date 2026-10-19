using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.VehicleDocuments;
using PMS.Application.Models.VehicleDocuments;
using PMS.Application.Services.Ocr;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

public class VehicleDocumentService(
    PmsDbContext pmsContext,
    IVehicleDocumentStorage storage,
    VehicleDocumentStorageOptions storageOptions,
    IVehicleVerificationQueue queue,
    IVehicleVerificationNotifier notifier = null) : IVehicleDocumentService
{
    private static readonly string[] ActiveStatuses = [VehicleVerificationStatus.Pending, VehicleVerificationStatus.Processing];

    public async Task<VehicleVerificationModel> UploadAsync(string userId, Guid vehicleId, string documentType, string side,
        Stream content, string fileName, long length)
    {
        documentType = VehicleDocumentKinds.Types.FirstOrDefault(t => t.Equals(documentType, StringComparison.OrdinalIgnoreCase))
                       ?? throw new BadRequestException("Document type must be RegistrationCertificate or CitizenId.");
        side = VehicleDocumentKinds.Sides.FirstOrDefault(s => s.Equals(side, StringComparison.OrdinalIgnoreCase))
               ?? throw new BadRequestException("Side must be Front or Back.");

        if (content == null || length <= 0)
            throw new BadRequestException("Choose a file to upload.");
        if (length > storageOptions.MaxFileBytes)
            throw new BadRequestException($"The file is larger than {storageOptions.MaxFileBytes / (1024 * 1024)} MB.");

        // The type comes from the file's own bytes, never from its name or the browser's header
        var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        if (buffer.Length > storageOptions.MaxFileBytes)
            throw new BadRequestException($"The file is larger than {storageOptions.MaxFileBytes / (1024 * 1024)} MB.");
        var contentType = DetectContentType(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))
                          ?? throw new BadRequestException("Only JPEG, PNG or PDF files are accepted.");

        var (vehicle, customer) = await FindOwnVehicleAsync(userId, vehicleId);

        var verification = await LatestAsync(vehicle.Id);
        if (verification != null && ActiveStatuses.Contains(verification.Status))
            throw new BadRequestException("Your papers are being checked. Wait for the result before uploading again.");

        if (verification is not { Status: VehicleVerificationStatus.Draft })
        {
            verification = new VehicleVerification
            {
                VehicleId = vehicle.Id,
                CustomerId = customer.Id,
                UserId = userId,
                Status = VehicleVerificationStatus.Draft,
                CreatedAt = DateTime.UtcNow
            };
            pmsContext.VehicleVerifications.Add(verification);
        }

        // A new photo of the same side replaces the old one
        var replaced = verification.Documents.FirstOrDefault(d => d.DocumentType == documentType && d.Side == side);
        if (replaced != null)
        {
            storage.Delete(replaced.StoragePath);
            verification.Documents.Remove(replaced);
            pmsContext.VehicleDocuments.Remove(replaced);
        }

        buffer.Position = 0;
        var storagePath = await storage.SaveAsync(buffer, verification.Id.ToString("N"));
        var document = new VehicleDocument
        {
            VerificationId = verification.Id,
            DocumentType = documentType,
            Side = side,
            StoragePath = storagePath,
            OriginalFileName = SafeFileName(fileName),
            ContentType = contentType,
            SizeBytes = buffer.Length,
            UploadedAt = DateTime.UtcNow
        };
        // Added explicitly: the key is set client-side, so EF must not guess the entity already exists
        pmsContext.VehicleDocuments.Add(document);
        if (!verification.Documents.Contains(document))
            verification.Documents.Add(document);

        await pmsContext.SaveChangesAsync();
        return ToModel<VehicleVerificationModel>(verification, vehicle, storageOptions);
    }

    public async Task<VehicleVerificationModel> SubmitAsync(string userId, Guid vehicleId)
    {
        var (vehicle, _) = await FindOwnVehicleAsync(userId, vehicleId);
        var verification = await LatestAsync(vehicle.Id);

        if (verification is not { Status: VehicleVerificationStatus.Draft })
            throw new BadRequestException("Upload the registration certificate and Citizen ID photos first.");

        var missing = MissingDocuments(verification);
        if (missing.Count > 0)
            throw new BadRequestException($"Upload every side first; missing: {string.Join(", ", missing)}.");

        verification.Status = VehicleVerificationStatus.Pending;
        verification.SubmittedAt = DateTime.UtcNow;
        await pmsContext.SaveChangesAsync();

        // OCR runs in the background (Hangfire), so the request returns at once
        queue.Enqueue(verification.Id);
        if (notifier != null)
            await notifier.VerificationChangedAsync(userId, verification.Id, vehicle.Id, verification.Status);

        return ToModel<VehicleVerificationModel>(verification, vehicle, storageOptions);
    }

    public async Task<VehicleVerificationModel> GetLatestAsync(string userId, Guid vehicleId)
    {
        var (vehicle, _) = await FindOwnVehicleAsync(userId, vehicleId);
        var verification = await LatestAsync(vehicle.Id);
        return verification == null ? null : ToModel<VehicleVerificationModel>(verification, vehicle, storageOptions);
    }

    public async Task<VehicleDocumentContent> OpenFileAsync(string userId, Guid vehicleId, Guid documentId)
    {
        var (vehicle, _) = await FindOwnVehicleAsync(userId, vehicleId);
        var document = await pmsContext.VehicleDocuments
            .FirstOrDefaultAsync(d => d.Id == documentId && d.Verification.VehicleId == vehicle.Id && d.Verification.UserId == userId);

        return Open(document);
    }

    public async Task<List<VehicleVerificationReviewModel>> GetReviewQueueAsync(string status)
    {
        status = string.IsNullOrWhiteSpace(status) ? VehicleVerificationStatus.NeedsReview : status.Trim();

        var verifications = await pmsContext.VehicleVerifications
            .Include(v => v.Documents)
            .Include(v => v.Vehicle).ThenInclude(v => v.VehicleType)
            .Include(v => v.Vehicle).ThenInclude(v => v.Customer)
            .Where(v => v.Status == status)
            .OrderBy(v => v.SubmittedAt ?? v.CreatedAt)
            .Take(200)
            .ToListAsync();

        return verifications.Select(v => ToReviewModel(v, storageOptions)).ToList();
    }

    public async Task<VehicleVerificationReviewModel> GetForReviewAsync(Guid verificationId)
    {
        return ToReviewModel(await FindForReviewAsync(verificationId), storageOptions);
    }

    public async Task<VehicleDocumentContent> OpenFileForReviewAsync(Guid verificationId, Guid documentId)
    {
        var document = await pmsContext.VehicleDocuments
            .FirstOrDefaultAsync(d => d.Id == documentId && d.VerificationId == verificationId);
        return Open(document);
    }

    public async Task<VehicleVerificationReviewModel> ReviewAsync(string staffUserId, Guid verificationId, ReviewVehicleVerificationModel model)
    {
        var verification = await FindForReviewAsync(verificationId);
        if (verification.Status != VehicleVerificationStatus.NeedsReview)
            throw new BadRequestException("Only checks waiting for review can be approved or rejected.");
        if (!model.Approve && string.IsNullOrWhiteSpace(model.Note))
            throw new BadRequestException("Give the customer a reason when rejecting.");

        verification.Status = model.Approve ? VehicleVerificationStatus.Approved : VehicleVerificationStatus.Rejected;
        verification.ReviewedBy = staffUserId;
        verification.ReviewNote = model.Note?.Trim();
        verification.DecidedAt = DateTime.UtcNow;
        await pmsContext.SaveChangesAsync();

        if (notifier != null)
            await notifier.VerificationChangedAsync(verification.UserId, verification.Id, verification.VehicleId, verification.Status);

        return ToReviewModel(verification, storageOptions);
    }

    /// <summary>JPEG, PNG or PDF by magic number; null for anything else.</summary>
    public static string DetectContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return "image/png";
        if (bytes.Length >= 5 && bytes[..5].SequenceEqual("%PDF-"u8))
            return "application/pdf";
        return null;
    }

    /// <summary>The sides still needed: front and back of both papers.</summary>
    public static List<string> MissingDocuments(VehicleVerification verification) =>
        (from type in VehicleDocumentKinds.Types
         from side in VehicleDocumentKinds.Sides
         where !verification.Documents.Any(d => d.DocumentType == type && d.Side == side)
         select $"{type}/{side}").ToList();

    internal static T ToModel<T>(VehicleVerification v, Vehicle vehicle, VehicleDocumentStorageOptions options) where T : VehicleVerificationModel, new()
    {
        return new T
        {
            VerificationId = v.Id,
            VehicleId = v.VehicleId,
            LicensePlate = vehicle?.PlateNumber,
            Status = v.Status,
            StatusReason = v.StatusReason,
            Confidence = v.Confidence == null ? null : (double)v.Confidence.Value,
            Fields = string.IsNullOrEmpty(v.ResultJson) ? [] : JsonSerializer.Deserialize<List<FieldCheck>>(v.ResultJson),
            Documents = v.Documents
                .OrderBy(d => Array.IndexOf(VehicleDocumentKinds.Types, d.DocumentType))
                .ThenBy(d => Array.IndexOf(VehicleDocumentKinds.Sides, d.Side))
                .Select(d => new VehicleDocumentFileModel
                {
                    DocumentId = d.Id,
                    DocumentType = d.DocumentType,
                    Side = d.Side,
                    FileName = d.OriginalFileName,
                    ContentType = d.ContentType,
                    SizeBytes = d.SizeBytes,
                    UploadedAt = d.UploadedAt,
                    IsAvailable = d.StoragePath != null
                }).ToList(),
            MissingDocuments = v.Status == VehicleVerificationStatus.Draft ? MissingDocuments(v) : [],
            CreatedAt = v.CreatedAt,
            SubmittedAt = v.SubmittedAt,
            ProcessedAt = v.ProcessedAt,
            DecidedAt = v.DecidedAt,
            ReviewNote = v.ReviewNote,
            FilesDeleteAt = v.FilesDeletedAt ?? v.DecidedAt?.AddDays(options.RetentionDays),
            FilesDeletedAt = v.FilesDeletedAt
        };
    }

    private static VehicleVerificationReviewModel ToReviewModel(VehicleVerification v, VehicleDocumentStorageOptions options)
    {
        var model = ToModel<VehicleVerificationReviewModel>(v, v.Vehicle, options);
        model.CustomerName = v.Vehicle?.Customer?.FullName;
        model.CustomerCitizenId = v.Vehicle?.Customer?.CitizenId;
        model.VehicleType = v.Vehicle?.VehicleType?.Name;
        model.Brand = v.Vehicle?.Brand;
        model.Model = v.Vehicle?.Model;
        model.Color = v.Vehicle?.Color;
        model.OwnerCitizenId = v.Vehicle?.CitizenId;
        return model;
    }

    private async Task<(Vehicle Vehicle, Customer Customer)> FindOwnVehicleAsync(string userId, Guid vehicleId)
    {
        var customer = await pmsContext.Customers.FirstOrDefaultAsync(c => c.UserId == userId);
        var vehicle = customer == null
            ? null
            : await pmsContext.Vehicles.FirstOrDefaultAsync(v => v.Id == vehicleId && v.CustomerId == customer.Id && v.IsActive);

        // Someone else's vehicle looks exactly like a missing one
        if (vehicle == null)
            throw new NotFoundException("Vehicle not found.");

        return (vehicle, customer);
    }

    private Task<VehicleVerification> LatestAsync(Guid vehicleId) =>
        pmsContext.VehicleVerifications
            .Include(v => v.Documents)
            .Where(v => v.VehicleId == vehicleId)
            .OrderByDescending(v => v.CreatedAt)
            .FirstOrDefaultAsync();

    private async Task<VehicleVerification> FindForReviewAsync(Guid verificationId) =>
        await pmsContext.VehicleVerifications
            .Include(v => v.Documents)
            .Include(v => v.Vehicle).ThenInclude(v => v.VehicleType)
            .Include(v => v.Vehicle).ThenInclude(v => v.Customer)
            .FirstOrDefaultAsync(v => v.Id == verificationId)
        ?? throw new NotFoundException("Verification not found.");

    private VehicleDocumentContent Open(VehicleDocument document)
    {
        if (document == null)
            throw new NotFoundException("Document not found.");

        var stream = storage.OpenRead(document.StoragePath)
                     ?? throw new NotFoundException("The photo was deleted after the verification was decided.");
        return new VehicleDocumentContent(stream, document.ContentType, document.OriginalFileName ?? $"{document.DocumentType}-{document.Side}");
    }

    private static string SafeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        return name.Length > 200 ? name[..200] : name;
    }
}
