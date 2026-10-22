using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Jobs;
using PMS.Application.Models.VehicleDocuments;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.VehicleDocuments;

/// <summary>The BR-26 deletion of vehicle paper photos.</summary>
public class VehicleDocumentJobsTests : IDisposable
{
    private readonly PmsDbContext _context;
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), $"pms-doc-jobs-{Guid.NewGuid():N}");
    private readonly LocalVehicleDocumentStorage _storage;
    private readonly VehicleDocumentJobs _sut;

    public VehicleDocumentJobsTests()
    {
        _context = new PmsDbContext(new DbContextOptionsBuilder<PmsDbContext>()
            .UseInMemoryDatabase($"VehicleDocumentJobsTests_{Guid.NewGuid()}")
            .Options);
        var storageOptions = new VehicleDocumentStorageOptions { StorageRoot = _storageRoot };
        _storage = new LocalVehicleDocumentStorage(storageOptions, _storageRoot);
        _sut = new VehicleDocumentJobs(_context, _storage, storageOptions);
    }

    public void Dispose()
    {
        _context.Dispose();
        if (Directory.Exists(_storageRoot))
            Directory.Delete(_storageRoot, true);
    }

    /// <summary>A check with the three required photos stored on disk.</summary>
    private async Task<VehicleVerification> SeedAsync(string status, DateTime createdAt)
    {
        var customer = new Customer { UserId = "user-1", CustomerCode = "C1", FullName = "Nguyễn Tấn Phát", CitizenId = "079204006789", IsActive = true };
        var vehicle = new Vehicle
        {
            Customer = customer, VehicleTypeId = 1, PlateNumber = "51K-992.88", PlateNormalized = "51K99288",
            Brand = "Toyota", Model = "Camry", Color = "White", CitizenId = "079204006789", IsActive = true
        };
        var verification = new VehicleVerification { Vehicle = vehicle, UserId = "user-1", Status = status, CreatedAt = createdAt };
        _context.AddRange(customer, vehicle, verification);

        foreach (var (type, side) in VehicleDocumentKinds.Required)
        {
            var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            _context.VehicleDocuments.Add(new VehicleDocument
            {
                Verification = verification, DocumentType = type, Side = side, ContentType = "image/png", SizeBytes = bytes.Length,
                StoragePath = await _storage.SaveAsync(new MemoryStream(bytes), verification.Id.ToString("N"))
            });
        }

        await _context.SaveChangesAsync();
        return verification;
    }

    [Fact]
    public async Task PurgeExpiredDocumentsAsync_Should_Delete_Photos_30_Days_After_The_Decision_Only()
    {
        var old = await SeedAsync(VehicleVerificationStatus.Approved, DateTime.UtcNow.AddDays(-40));
        old.DecidedAt = DateTime.UtcNow.AddDays(-31);
        await _context.SaveChangesAsync();
        var oldPaths = old.Documents.Select(d => d.StoragePath).ToList();

        var deleted = await _sut.PurgeExpiredDocumentsAsync();

        deleted.Should().Be(3);
        old.FilesDeletedAt.Should().NotBeNull();
        old.Documents.Should().OnlyContain(d => d.StoragePath == null);
        oldPaths.Should().OnlyContain(p => _storage.OpenRead(p) == null);
    }

    [Fact]
    public async Task PurgeExpiredDocumentsAsync_Should_Keep_Recent_Decisions_And_Waiting_Checks()
    {
        var recent = await SeedAsync(VehicleVerificationStatus.Rejected, DateTime.UtcNow.AddDays(-35));
        recent.DecidedAt = DateTime.UtcNow.AddDays(-29);
        var waiting = await SeedAsync(VehicleVerificationStatus.NeedsReview, DateTime.UtcNow.AddDays(-45));
        await _context.SaveChangesAsync();

        (await _sut.PurgeExpiredDocumentsAsync()).Should().Be(0);
        recent.Documents.Should().OnlyContain(d => d.StoragePath != null);
        waiting.Documents.Should().OnlyContain(d => d.StoragePath != null);
    }

    [Fact]
    public async Task PurgeExpiredDocumentsAsync_Should_Remove_Abandoned_Drafts()
    {
        await SeedAsync(VehicleVerificationStatus.Draft, DateTime.UtcNow.AddDays(-31));

        (await _sut.PurgeExpiredDocumentsAsync()).Should().Be(3);
        (await _context.VehicleVerifications.CountAsync()).Should().Be(0);
    }
}
