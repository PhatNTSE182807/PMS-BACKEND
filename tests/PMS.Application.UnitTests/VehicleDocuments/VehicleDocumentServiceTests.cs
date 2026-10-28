using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using PMS.Application.Exceptions;
using PMS.Application.Models.VehicleDocuments;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.VehicleDocuments;

/// <summary>Upload, submit and Staff review of vehicle papers.</summary>
public class VehicleDocumentServiceTests : IDisposable
{
    private const string UserId = "user-1";
    private const string StaffId = "staff-1";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0];
    private static readonly byte[] Pdf = "%PDF-1.7"u8.ToArray();

    private readonly PmsDbContext _context;
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), $"pms-doc-svc-{Guid.NewGuid():N}");
    private readonly VehicleDocumentStorageOptions _options;
    private readonly LocalVehicleDocumentStorage _storage;
    private readonly IVehicleVerificationNotifier _notifier = Substitute.For<IVehicleVerificationNotifier>();
    private readonly IUserNotificationPublisher _notifications = Substitute.For<IUserNotificationPublisher>();
    private readonly VehicleDocumentService _sut;

    private Customer _customer;
    private Vehicle _vehicle;

    public VehicleDocumentServiceTests()
    {
        _context = new PmsDbContext(new DbContextOptionsBuilder<PmsDbContext>()
            .UseInMemoryDatabase($"VehicleDocumentServiceTests_{Guid.NewGuid()}")
            .Options);
        _options = new VehicleDocumentStorageOptions { StorageRoot = _storageRoot, MaxFileBytes = 1024 };
        _storage = new LocalVehicleDocumentStorage(_options, _storageRoot);
        _sut = new VehicleDocumentService(_context, _storage, _options, _notifier, _notifications);
        Seed();
    }

    public void Dispose()
    {
        _context.Dispose();
        if (Directory.Exists(_storageRoot))
            Directory.Delete(_storageRoot, true);
    }

    private void Seed()
    {
        var type = new VehicleType { Code = "CAR_4", Name = "4-Seat Car", IsActive = true };
        _customer = new Customer { UserId = UserId, CustomerCode = "C1", FullName = "Customer One", CitizenId = "079204006789", IsActive = true };
        _vehicle = new Vehicle
        {
            Customer = _customer, VehicleType = type, PlateNumber = "51K-992.88", PlateNormalized = "51K99288",
            Brand = "Toyota", Model = "Camry", Color = "White", IsActive = true, IsDefault = true, CreatedAt = DateTime.UtcNow
        };
        _context.AddRange(type, _customer, _vehicle);
        _context.SaveChanges();
    }

    private Task<VehicleVerificationModel> UploadAsync(string type, string side, byte[] bytes, string fileName = "photo.png") =>
        _sut.UploadAsync(UserId, _vehicle.Id, type, side, new MemoryStream(bytes), fileName, bytes.Length);

    private async Task<VehicleVerificationModel> SubmittedAsync()
    {
        await UploadAsync("registrationcertificate", "front", Png);
        await UploadAsync(VehicleDocumentKinds.CitizenId, VehicleDocumentKinds.Front, Jpeg, "id.jpg");
        return await _sut.SubmitAsync(UserId, _vehicle.Id);
    }

    // ---- Content detection ----

    [Fact]
    public void DetectContentType_Should_Read_The_Magic_Number()
    {
        VehicleDocumentService.DetectContentType(Png).Should().Be("image/png");
        VehicleDocumentService.DetectContentType(Jpeg).Should().Be("image/jpeg");
        VehicleDocumentService.DetectContentType(Pdf).Should().Be("application/pdf");
        VehicleDocumentService.DetectContentType("GIF89a"u8).Should().BeNull();
        VehicleDocumentService.DetectContentType([]).Should().BeNull();
    }

    // ---- Upload ----

    [Theory]
    [InlineData("Passport", "Front")]
    [InlineData("CitizenId", "Top")]
    [InlineData("CitizenId", "Back")]
    public async Task UploadAsync_Should_Reject_Unknown_Or_Unneeded_Papers(string type, string side)
    {
        var act = () => UploadAsync(type, side, Png);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task UploadAsync_Should_Reject_Empty_Large_And_Unknown_Files()
    {
        await FluentActions.Invoking(() => _sut.UploadAsync(UserId, _vehicle.Id, "CitizenId", "Front", null, "a.png", 0))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Choose a file to upload.");

        await FluentActions.Invoking(() => _sut.UploadAsync(UserId, _vehicle.Id, "CitizenId", "Front", new MemoryStream(Png), "a.png", 5000))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*larger than*");

        // The declared length lies: the stream itself is too big
        var big = Png.Concat(new byte[2000]).ToArray();
        await FluentActions.Invoking(() => _sut.UploadAsync(UserId, _vehicle.Id, "CitizenId", "Front", new MemoryStream(big), "a.png", 10))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*larger than*");

        await FluentActions.Invoking(() => UploadAsync("CitizenId", "Front", "GIF89a"u8.ToArray(), "a.gif"))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Only JPEG, PNG or PDF files are accepted.");
    }

    [Fact]
    public async Task UploadAsync_Should_Hide_Other_Customers_Vehicles()
    {
        var act = () => _sut.UploadAsync("someone-else", _vehicle.Id, "CitizenId", "Front", new MemoryStream(Png), "a.png", Png.Length);

        await act.Should().ThrowAsync<NotFoundException>().WithMessage("Vehicle not found.");
    }

    [Fact]
    public async Task UploadAsync_Should_Start_A_Draft_And_Replace_The_Same_Side()
    {
        var first = await UploadAsync("CitizenId", "Front", Png, "first.png");
        first.Status.Should().Be(VehicleVerificationStatus.Draft);
        first.MissingDocuments.Should().Equal("RegistrationCertificate/Front");

        var second = await UploadAsync("CitizenId", "Front", Pdf, @"C:\folder\second.pdf");

        second.VerificationId.Should().Be(first.VerificationId);
        second.Documents.Should().ContainSingle();
        second.Documents[0].FileName.Should().Be("second.pdf");
        second.Documents[0].ContentType.Should().Be("application/pdf");
        _context.VehicleDocuments.Count().Should().Be(1);
    }

    [Fact]
    public async Task UploadAsync_Should_Cut_Long_File_Names()
    {
        var model = await UploadAsync("CitizenId", "Front", Png, new string('a', 300) + ".png");

        model.Documents[0].FileName.Should().HaveLength(200);
    }

    [Fact]
    public async Task UploadAsync_Should_Refuse_While_Waiting_Or_After_Approval()
    {
        var submitted = await SubmittedAsync();

        await FluentActions.Invoking(() => UploadAsync("CitizenId", "Front", Png))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*waiting for Staff review*");

        await _sut.ReviewAsync(StaffId, submitted.VerificationId, new ReviewVehicleVerificationModel { Approve = true });

        await FluentActions.Invoking(() => UploadAsync("CitizenId", "Front", Png))
            .Should().ThrowAsync<BadRequestException>().WithMessage("This vehicle is already approved.");
    }

    // ---- Submit ----

    [Fact]
    public async Task SubmitAsync_Should_Need_A_Draft_With_Every_Paper()
    {
        await FluentActions.Invoking(() => _sut.SubmitAsync(UserId, _vehicle.Id))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Upload the registration certificate*");

        await UploadAsync("CitizenId", "Front", Png);

        await FluentActions.Invoking(() => _sut.SubmitAsync(UserId, _vehicle.Id))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*missing: RegistrationCertificate/Front*");
    }

    [Fact]
    public async Task SubmitAsync_Should_Queue_For_Review_And_Notify()
    {
        var model = await SubmittedAsync();

        model.Status.Should().Be(VehicleVerificationStatus.NeedsReview);
        model.SubmittedAt.Should().NotBeNull();
        model.MissingDocuments.Should().BeEmpty();
        model.Documents.Select(d => d.DocumentType).Should().Equal("RegistrationCertificate", "CitizenId");
        await _notifier.Received(1).VerificationChangedAsync(UserId, model.VerificationId, _vehicle.Id, VehicleVerificationStatus.NeedsReview);
    }

    [Fact]
    public async Task SubmitAsync_Should_Work_Without_A_Notifier()
    {
        var sut = new VehicleDocumentService(_context, _storage, _options);
        await UploadAsync("RegistrationCertificate", "Front", Png);
        await UploadAsync("CitizenId", "Front", Png);

        var model = await sut.SubmitAsync(UserId, _vehicle.Id);

        model.Status.Should().Be(VehicleVerificationStatus.NeedsReview);
    }

    // ---- Reading ----

    [Fact]
    public async Task GetLatestAsync_Should_Return_Null_Before_Any_Upload_Then_The_Latest_Check()
    {
        (await _sut.GetLatestAsync(UserId, _vehicle.Id)).Should().BeNull();

        await UploadAsync("CitizenId", "Front", Png);

        (await _sut.GetLatestAsync(UserId, _vehicle.Id)).Status.Should().Be(VehicleVerificationStatus.Draft);
    }

    [Fact]
    public async Task OpenFileAsync_Should_Stream_The_Owners_Photo()
    {
        var model = await UploadAsync("CitizenId", "Front", Png, "id.png");

        var file = await _sut.OpenFileAsync(UserId, _vehicle.Id, model.Documents[0].DocumentId);

        file.ContentType.Should().Be("image/png");
        file.FileName.Should().Be("id.png");
        using var reader = new MemoryStream();
        await file.Content.CopyToAsync(reader);
        file.Content.Dispose();
        reader.ToArray().Should().Equal(Png);
    }

    [Fact]
    public async Task OpenFileAsync_Should_Throw_For_Unknown_Or_Deleted_Files()
    {
        var model = await UploadAsync("CitizenId", "Front", Png);

        await FluentActions.Invoking(() => _sut.OpenFileAsync(UserId, _vehicle.Id, Guid.NewGuid()))
            .Should().ThrowAsync<NotFoundException>().WithMessage("Document not found.");

        var document = _context.VehicleDocuments.Single();
        _storage.Delete(document.StoragePath);
        document.StoragePath = null;
        document.OriginalFileName = null;
        await _context.SaveChangesAsync();

        await FluentActions.Invoking(() => _sut.OpenFileForReviewAsync(model.VerificationId, document.Id))
            .Should().ThrowAsync<NotFoundException>().WithMessage("*deleted*");
    }

    // ---- Staff review ----

    [Fact]
    public async Task GetReviewQueueAsync_Should_List_Waiting_Approved_And_Rejected_Checks()
    {
        var submitted = await SubmittedAsync();

        var waiting = await _sut.GetReviewQueueAsync(null);
        waiting.Should().ContainSingle(v => v.VerificationId == submitted.VerificationId);
        waiting[0].CustomerName.Should().Be("Customer One");
        waiting[0].VehicleType.Should().Be("4-Seat Car");
        waiting[0].Brand.Should().Be("Toyota");

        (await _sut.GetReviewQueueAsync(VehicleVerificationStatus.Approved)).Should().BeEmpty();

        await _sut.ReviewAsync(StaffId, submitted.VerificationId, new ReviewVehicleVerificationModel { Approve = true, Note = "  ok  " });

        var approved = await _sut.GetReviewQueueAsync(" Approved ");
        approved.Should().ContainSingle();
        approved[0].ReviewNote.Should().Be("ok");
        approved[0].FilesDeleteAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(_options.RetentionDays), TimeSpan.FromMinutes(1));
        (await _sut.GetReviewQueueAsync(VehicleVerificationStatus.Rejected)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetForReviewAsync_Should_Throw_For_An_Unknown_Check()
    {
        await FluentActions.Invoking(() => _sut.GetForReviewAsync(Guid.NewGuid()))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task GetForReviewAsync_And_OpenFileForReviewAsync_Should_Show_The_Check()
    {
        var submitted = await SubmittedAsync();

        var review = await _sut.GetForReviewAsync(submitted.VerificationId);
        var file = await _sut.OpenFileForReviewAsync(submitted.VerificationId, review.Documents[0].DocumentId);
        file.Content.Dispose();

        review.LicensePlate.Should().Be("51K-992.88");
        file.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task ReviewAsync_Should_Need_A_Waiting_Check_And_A_Reason_To_Reject()
    {
        var draft = await UploadAsync("CitizenId", "Front", Png);

        await FluentActions.Invoking(() => _sut.ReviewAsync(StaffId, draft.VerificationId, new ReviewVehicleVerificationModel { Approve = true }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Only registrations waiting for review*");

        await UploadAsync("RegistrationCertificate", "Front", Png);
        var submitted = await _sut.SubmitAsync(UserId, _vehicle.Id);

        await FluentActions.Invoking(() => _sut.ReviewAsync(StaffId, submitted.VerificationId, new ReviewVehicleVerificationModel { Approve = false, Note = " " }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Give the customer a reason when rejecting.");
    }

    [Fact]
    public async Task ReviewAsync_Approve_Should_Notify_The_Owner()
    {
        var submitted = await SubmittedAsync();

        var review = await _sut.ReviewAsync(StaffId, submitted.VerificationId, new ReviewVehicleVerificationModel { Approve = true });

        review.Status.Should().Be(VehicleVerificationStatus.Approved);
        _vehicle.IsActive.Should().BeTrue();
        await _notifier.Received(1).VerificationChangedAsync(UserId, submitted.VerificationId, _vehicle.Id, VehicleVerificationStatus.Approved);
        _notifications.Received(1).VehicleReviewed(submitted.VerificationId);
    }

    [Fact]
    public async Task ReviewAsync_Reject_Should_Remove_The_Vehicle_And_Move_The_Default()
    {
        var other = new Vehicle
        {
            CustomerId = _customer.Id, VehicleTypeId = _vehicle.VehicleTypeId, PlateNumber = "30E-123.45", PlateNormalized = "30E12345",
            IsActive = true, CreatedAt = DateTime.UtcNow
        };
        _context.Vehicles.Add(other);
        await _context.SaveChangesAsync();
        var submitted = await SubmittedAsync();

        var review = await _sut.ReviewAsync(StaffId, submitted.VerificationId, new ReviewVehicleVerificationModel { Approve = false, Note = "Blurry photo" });

        review.Status.Should().Be(VehicleVerificationStatus.Rejected);
        _vehicle.IsActive.Should().BeFalse();
        _vehicle.IsDefault.Should().BeFalse();
        other.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task ReviewAsync_Reject_Of_A_Non_Default_Vehicle_Should_Keep_Other_Defaults()
    {
        _vehicle.IsDefault = false;
        await _context.SaveChangesAsync();
        var submitted = await SubmittedAsync();

        await _sut.ReviewAsync(StaffId, submitted.VerificationId, new ReviewVehicleVerificationModel { Approve = false, Note = "Wrong owner" });

        _vehicle.IsActive.Should().BeFalse();
    }

    // ---- Rejected notices ----

    [Fact]
    public async Task GetRecentlyRejectedAsync_Should_Return_Empty_For_A_User_Without_Customer()
    {
        (await _sut.GetRecentlyRejectedAsync("nobody")).Should().BeEmpty();
    }

    [Fact]
    public async Task GetRecentlyRejectedAsync_Should_List_Rejections_Until_The_Plate_Is_Registered_Again()
    {
        var submitted = await SubmittedAsync();
        await _sut.ReviewAsync(StaffId, submitted.VerificationId, new ReviewVehicleVerificationModel { Approve = false, Note = "Blurry photo" });

        var notices = await _sut.GetRecentlyRejectedAsync(UserId);

        notices.Should().ContainSingle();
        notices[0].LicensePlate.Should().Be("51K-992.88");
        notices[0].ReviewNote.Should().Be("Blurry photo");
        notices[0].Brand.Should().Be("Toyota");

        _context.Vehicles.Add(new Vehicle
        {
            CustomerId = _customer.Id, VehicleTypeId = _vehicle.VehicleTypeId, PlateNumber = "51K-992.88", PlateNormalized = "51K99288",
            IsActive = true, CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        (await _sut.GetRecentlyRejectedAsync(UserId)).Should().BeEmpty();
    }
}
