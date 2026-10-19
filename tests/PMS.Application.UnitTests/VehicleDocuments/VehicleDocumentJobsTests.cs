using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using PMS.Application.Helpers.VehicleDocuments;
using PMS.Application.Jobs;
using PMS.Application.Models.VehicleDocuments;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.Application.Services.Ocr;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.VehicleDocuments;

/// <summary>The background OCR job (with the fake engine) and the BR-26 photo deletion.</summary>
public class VehicleDocumentJobsTests : IDisposable
{
    private readonly PmsDbContext _context;
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), $"pms-doc-jobs-{Guid.NewGuid():N}");
    private readonly LocalVehicleDocumentStorage _storage;
    private readonly IVehicleVerificationNotifier _notifier = Substitute.For<IVehicleVerificationNotifier>();
    private readonly VehicleDocumentJobs _sut;

    public VehicleDocumentJobsTests()
    {
        _context = new PmsDbContext(new DbContextOptionsBuilder<PmsDbContext>()
            .UseInMemoryDatabase($"VehicleDocumentJobsTests_{Guid.NewGuid()}")
            .Options);
        var storageOptions = new VehicleDocumentStorageOptions { StorageRoot = _storageRoot };
        _storage = new LocalVehicleDocumentStorage(storageOptions, _storageRoot);
        _sut = new VehicleDocumentJobs(_context, _storage, new FakeDocumentOcrService(), new OcrOptions(), storageOptions, _notifier);
    }

    public void Dispose()
    {
        _context.Dispose();
        if (Directory.Exists(_storageRoot))
            Directory.Delete(_storageRoot, true);
    }

    private const string CertificateText = """
        Tên chủ xe (Owner's full name): NGUYỄN TẤN PHÁT
        Nhãn hiệu (Brand): TOYOTA   Số loại (Model code): CAMRY 2.5Q
        Màu sơn (Color): TRẮNG
        Biển số đăng ký (Number plate): 51K-992.88
        """;

    private const string CardText = """
        Số / No.: 079204006789
        Họ và tên / Full name: NGUYỄN TẤN PHÁT
        """;

    /// <summary>A PNG header followed by the text the fake OCR "reads".</summary>
    private static byte[] FakePng(string text) =>
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }
            .Concat(Encoding.UTF8.GetBytes(FakeDocumentOcrService.Marker + "\n" + text))
            .ToArray();

    private async Task<VehicleVerification> SeedAsync(string certificateText, string cardText, string status = "Pending", string profileCitizenId = "079204006789")
    {
        var customer = new Customer { UserId = "user-1", CustomerCode = "C1", FullName = "Nguyễn Tấn Phát", CitizenId = profileCitizenId, IsActive = true };
        var vehicle = new Vehicle
        {
            Customer = customer, VehicleTypeId = 1, PlateNumber = "51K-992.88", PlateNormalized = "51K99288",
            Brand = "Toyota", Model = "Camry", Color = "White", CitizenId = profileCitizenId, IsActive = true
        };
        var verification = new VehicleVerification { Vehicle = vehicle, UserId = "user-1", Status = status, CreatedAt = DateTime.UtcNow };
        _context.AddRange(customer, vehicle, verification);

        foreach (var (type, text) in new[] { ("RegistrationCertificate", certificateText), ("CitizenId", cardText) })
        foreach (var side in new[] { "Front", "Back" })
        {
            var bytes = FakePng(side == "Front" ? text : string.Empty);
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
    public async Task ProcessAsync_Should_AutoVerify_Matching_Papers_And_Notify_The_Uploader()
    {
        var verification = await SeedAsync(CertificateText, CardText);

        await _sut.ProcessAsync(verification.Id);

        var saved = await _context.VehicleVerifications.SingleAsync();
        saved.Status.Should().Be(VehicleVerificationStatus.AutoVerified);
        saved.DecidedAt.Should().NotBeNull();
        saved.Confidence.Should().BeGreaterThan(0.85m);
        JsonSerializer.Deserialize<FieldCheck[]>(saved.ResultJson).Should().OnlyContain(f => f.Result == "Match");
        await _notifier.Received().VerificationChangedAsync("user-1", verification.Id, verification.VehicleId, VehicleVerificationStatus.AutoVerified);
    }

    [Fact]
    public async Task ProcessAsync_Should_Send_A_Card_That_Is_Not_The_Profiles_To_Review()
    {
        var verification = await SeedAsync(CertificateText, CardText.Replace("079204006789", "001099012345"));

        await _sut.ProcessAsync(verification.Id);

        var saved = await _context.VehicleVerifications.SingleAsync();
        saved.Status.Should().Be(VehicleVerificationStatus.NeedsReview);
        saved.DecidedAt.Should().BeNull();
        saved.StatusReason.Should().Contain("Citizen ID number");
    }

    [Fact]
    public async Task ProcessAsync_Should_Reject_Photos_Without_Readable_Text()
    {
        var verification = await SeedAsync(string.Empty, string.Empty);

        await _sut.ProcessAsync(verification.Id);

        (await _context.VehicleVerifications.SingleAsync()).Status.Should().Be(VehicleVerificationStatus.Rejected);
    }

    [Fact]
    public async Task ProcessAsync_Should_Send_To_Review_When_The_Engine_Fails()
    {
        var verification = await SeedAsync(CertificateText, CardText);
        var failing = Substitute.For<IDocumentOcrService>();
        failing.ReadAsync(default, default).ReturnsForAnyArgs(OcrResult.Failed("no traineddata"));
        var sut = new VehicleDocumentJobs(_context, _storage, failing, new OcrOptions(), new VehicleDocumentStorageOptions(), _notifier);

        await sut.ProcessAsync(verification.Id);

        var saved = await _context.VehicleVerifications.SingleAsync();
        saved.Status.Should().Be(VehicleVerificationStatus.NeedsReview);
        saved.StatusReason.Should().Contain("unavailable");
    }

    [Fact]
    public async Task ProcessAsync_Should_Skip_Checks_That_Are_Not_Pending()
    {
        var verification = await SeedAsync(CertificateText, CardText, status: VehicleVerificationStatus.Approved);

        await _sut.ProcessAsync(verification.Id);

        (await _context.VehicleVerifications.SingleAsync()).Status.Should().Be(VehicleVerificationStatus.Approved);
    }

    [Fact]
    public async Task PurgeExpiredDocumentsAsync_Should_Delete_Photos_30_Days_After_The_Decision_Only()
    {
        var old = await SeedAsync(CertificateText, CardText, status: VehicleVerificationStatus.Approved);
        old.DecidedAt = DateTime.UtcNow.AddDays(-31);
        await _context.SaveChangesAsync();
        var oldPaths = old.Documents.Select(d => d.StoragePath).ToList();

        var deleted = await _sut.PurgeExpiredDocumentsAsync();

        deleted.Should().Be(4);
        old.FilesDeletedAt.Should().NotBeNull();
        old.Documents.Should().OnlyContain(d => d.StoragePath == null);
        oldPaths.Should().OnlyContain(p => _storage.OpenRead(p) == null);
    }

    [Fact]
    public async Task PurgeExpiredDocumentsAsync_Should_Keep_Recent_Decisions()
    {
        var recent = await SeedAsync(CertificateText, CardText, status: VehicleVerificationStatus.Rejected);
        recent.DecidedAt = DateTime.UtcNow.AddDays(-29);
        await _context.SaveChangesAsync();

        (await _sut.PurgeExpiredDocumentsAsync()).Should().Be(0);
        recent.Documents.Should().OnlyContain(d => d.StoragePath != null);
    }
}
