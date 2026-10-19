using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PMS.API;
using PMS.Api.IntegrationTests.Common;
using PMS.Api.IntegrationTests.Helpers;
using PMS.Application.Models.Profile;
using PMS.Application.Models.VehicleDocuments;
using PMS.Application.Services.Ocr;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

/// <summary>
/// Upload of the cà vẹt and CCCD photos, OCR verification with the fake engine (it reads the text
/// embedded after a PNG header) and the Staff review of checks that need a person.
/// </summary>
public class VehicleDocumentsEndpointTests : IDisposable
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    private const string OwnCitizenId = "079204006789";

    private const string CertificateText = """
        Tên chủ xe (Owner's full name): NGUYỄN TẤN PHÁT
        Nhãn hiệu (Brand): PORSCHE   Số loại (Model code): TAYCAN 4S
        Màu sơn (Color): XANH
        Biển số đăng ký (Number plate): 51K-992.88
        """;

    private const string CardText = """
        Số / No.: 079204006789
        Họ và tên / Full name:
        NGUYỄN TẤN PHÁT
        """;

    public void Dispose() => _factory.Dispose();

    private static byte[] Png(string ocrText = "") =>
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }
            .Concat(Encoding.UTF8.GetBytes(FakeDocumentOcrService.Marker + "\n" + ocrText))
            .ToArray();

    private static MultipartFormDataContent FileContent(byte[] bytes, string fileName = "photo.png", string contentType = "image/png")
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { file, "file", fileName } };
    }

    /// <summary>A customer named Nguyễn Tấn Phát with Citizen ID 079204006789 and a Porsche Taycan 51K-992.88.</summary>
    private async Task<(HttpClient Client, Guid VehicleId)> CustomerWithVehicleAsync(string plate = "51K-992.88")
    {
        var client = await _factory.CreateClientWithRoleAsync("User");
        var context = _factory.GetRequiredService<PmsDbContext>();
        var type = await context.VehicleTypes.FirstOrDefaultAsync(t => t.Code == "CAR_4");
        if (type == null)
        {
            type = new VehicleType { Code = "CAR_4", Name = "4-Seat Car", IsActive = true };
            context.VehicleTypes.Add(type);
            await context.SaveChangesAsync();
        }

        var response = await client.PostAsync("/api/users/profile/vehicles", new JsonContent(new CreateVehicleModel
        {
            LicensePlate = plate, VehicleTypeId = type.Id, Brand = "Porsche", Model = "Taycan 4S",
            Color = "Gentian Blue Metallic", CitizenId = OwnCitizenId
        }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var vehicle = (await ResponseHelper.GetApiResultAsync<VehicleResponseModel>(response)).Result;

        // The profile the CCCD is compared with
        var customer = await context.Vehicles.Where(v => v.Id == vehicle.VehicleId).Select(v => v.Customer).SingleAsync();
        customer.FullName = "Nguyễn Tấn Phát";
        customer.CitizenId = OwnCitizenId;
        await context.SaveChangesAsync();

        return (client, vehicle.VehicleId);
    }

    private static string DocumentsUrl(Guid vehicleId) => $"/api/users/profile/vehicles/{vehicleId}/documents";

    private static async Task UploadAllAsync(HttpClient client, Guid vehicleId, string certificateText, string cardText)
    {
        foreach (var (type, text) in new[] { ("RegistrationCertificate", certificateText), ("CitizenId", cardText) })
        foreach (var side in new[] { "Front", "Back" })
        {
            var response = await client.PostAsync($"{DocumentsUrl(vehicleId)}/{type}/{side}", FileContent(Png(side == "Front" ? text : "")));
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }
    }

    private static async Task<VehicleVerificationModel> SubmitAsync(HttpClient client, Guid vehicleId)
    {
        var response = await client.PostAsync($"{DocumentsUrl(vehicleId)}/submit", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ResponseHelper.GetApiResultAsync<VehicleVerificationModel>(response)).Result;
    }

    private static async Task<VehicleVerificationModel> LatestAsync(HttpClient client, Guid vehicleId)
    {
        var response = await client.GetAsync(DocumentsUrl(vehicleId));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await ResponseHelper.GetApiResultAsync<VehicleVerificationModel>(response)).Result;
    }

    [Fact]
    public async Task Upload_Should_Reject_Files_That_Are_Not_Jpeg_Png_Or_Pdf()
    {
        var (client, vehicleId) = await CustomerWithVehicleAsync();

        // A text file renamed to .png with an image content type is still refused: the bytes decide
        var response = await client.PostAsync($"{DocumentsUrl(vehicleId)}/RegistrationCertificate/Front",
            FileContent(Encoding.UTF8.GetBytes("not an image"), "fake.png"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Only JPEG, PNG or PDF");
    }

    [Theory]
    [InlineData("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })]
    [InlineData("application/pdf", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37 })]
    public async Task Upload_Should_Accept_Jpeg_And_Pdf(string contentType, byte[] header)
    {
        var (client, vehicleId) = await CustomerWithVehicleAsync();

        var response = await client.PostAsync($"{DocumentsUrl(vehicleId)}/CitizenId/Back", FileContent(header, "card", contentType));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var verification = (await ResponseHelper.GetApiResultAsync<VehicleVerificationModel>(response)).Result;
        verification.Status.Should().Be(VehicleVerificationStatus.Draft);
        verification.Documents.Single().ContentType.Should().Be(contentType);
        verification.MissingDocuments.Should().HaveCount(3);
    }

    [Fact]
    public async Task Upload_Should_Reject_Files_Over_10_MB()
    {
        var (client, vehicleId) = await CustomerWithVehicleAsync();
        var big = Png().Concat(new byte[10 * 1024 * 1024]).ToArray();

        var response = await client.PostAsync($"{DocumentsUrl(vehicleId)}/RegistrationCertificate/Front", FileContent(big));

        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.RequestEntityTooLarge);
        _factory.GetRequiredService<PmsDbContext>().VehicleDocuments.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Passport", "Front")]
    [InlineData("CitizenId", "Inside")]
    public async Task Upload_Should_Reject_Unknown_Document_Types_And_Sides(string type, string side)
    {
        var (client, vehicleId) = await CustomerWithVehicleAsync();

        var response = await client.PostAsync($"{DocumentsUrl(vehicleId)}/{type}/{side}", FileContent(Png()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upload_Should_Not_Reach_Another_Users_Vehicle()
    {
        var (_, vehicleId) = await CustomerWithVehicleAsync();
        var stranger = await _factory.CreateClientWithRoleAsync("User");

        var upload = await stranger.PostAsync($"{DocumentsUrl(vehicleId)}/RegistrationCertificate/Front", FileContent(Png()));
        var read = await stranger.GetAsync(DocumentsUrl(vehicleId));
        var anonymous = await _factory.CreateUnauthorizedClient().PostAsync($"{DocumentsUrl(vehicleId)}/RegistrationCertificate/Front", FileContent(Png()));

        upload.StatusCode.Should().Be(HttpStatusCode.NotFound);
        read.StatusCode.Should().Be(HttpStatusCode.NotFound);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Submit_Should_Need_Every_Side()
    {
        var (client, vehicleId) = await CustomerWithVehicleAsync();
        await client.PostAsync($"{DocumentsUrl(vehicleId)}/RegistrationCertificate/Front", FileContent(Png(CertificateText)));

        var response = await client.PostAsync($"{DocumentsUrl(vehicleId)}/submit", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("CitizenId/Front");
    }

    [Fact]
    public async Task Matching_Papers_Should_Be_AutoVerified()
    {
        var (client, vehicleId) = await CustomerWithVehicleAsync();
        await UploadAllAsync(client, vehicleId, CertificateText, CardText);

        await SubmitAsync(client, vehicleId);
        var result = await LatestAsync(client, vehicleId);

        result.Status.Should().Be(VehicleVerificationStatus.AutoVerified);
        result.Fields.Should().HaveCount(7).And.OnlyContain(f => f.Result == "Match");
        result.Fields.Single(f => f.Field == "plate").Read.Should().Be("51K-992.88");
        result.DecidedAt.Should().NotBeNull();
        result.FilesDeleteAt.Should().BeCloseTo(result.DecidedAt!.Value.AddDays(30), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Replacing_A_Side_Should_Keep_One_File_Per_Side()
    {
        var (client, vehicleId) = await CustomerWithVehicleAsync();
        await UploadAllAsync(client, vehicleId, "blurry", CardText);
        await client.PostAsync($"{DocumentsUrl(vehicleId)}/RegistrationCertificate/Front", FileContent(Png(CertificateText)));

        var draft = await LatestAsync(client, vehicleId);
        await SubmitAsync(client, vehicleId);

        draft.Documents.Should().HaveCount(4);
        (await LatestAsync(client, vehicleId)).Status.Should().Be(VehicleVerificationStatus.AutoVerified);
    }

    [Fact]
    public async Task Mismatched_Papers_Should_Wait_For_Staff_Who_Can_Approve_Them()
    {
        var (client, vehicleId) = await CustomerWithVehicleAsync();
        await UploadAllAsync(client, vehicleId, CertificateText.Replace("51K-992.88", "51K-992.89"), CardText);
        var verification = await SubmitAsync(client, vehicleId);

        var result = await LatestAsync(client, vehicleId);
        result.Status.Should().Be(VehicleVerificationStatus.NeedsReview);
        result.Fields.Single(f => f.Field == "plate").Result.Should().Be("Mismatch");

        // Customers cannot review; Staff see it in their queue
        (await client.GetAsync("/api/vehicle-verifications")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var staff = await _factory.CreateClientWithRoleAsync("Staff");
        var queue = await ResponseHelper.GetApiResultAsync<VehicleVerificationReviewModel[]>(await staff.GetAsync("/api/vehicle-verifications"));
        queue.Result.Should().ContainSingle(v => v.VerificationId == verification.VerificationId)
            .Which.CustomerCitizenId.Should().Be(OwnCitizenId);

        // A rejection needs a reason; an approval decides the check
        var noReason = await staff.PostAsync($"/api/vehicle-verifications/{verification.VerificationId}/review",
            new JsonContent(new ReviewVehicleVerificationModel { Approve = false }));
        noReason.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var approve = await staff.PostAsync($"/api/vehicle-verifications/{verification.VerificationId}/review",
            new JsonContent(new ReviewVehicleVerificationModel { Approve = true, Note = "Plate photo checked by hand." }));
        approve.StatusCode.Should().Be(HttpStatusCode.OK);

        var decided = await LatestAsync(client, vehicleId);
        decided.Status.Should().Be(VehicleVerificationStatus.Approved);
        decided.ReviewNote.Should().Be("Plate photo checked by hand.");
    }

    [Fact]
    public async Task Owner_And_Staff_Can_Download_A_Photo_But_Others_Cannot()
    {
        var (client, vehicleId) = await CustomerWithVehicleAsync();
        await UploadAllAsync(client, vehicleId, CertificateText, CardText);
        var document = (await LatestAsync(client, vehicleId)).Documents.First();
        var fileUrl = $"{DocumentsUrl(vehicleId)}/files/{document.DocumentId}";

        var own = await client.GetAsync(fileUrl);
        var stranger = await (await _factory.CreateClientWithRoleAsync("User")).GetAsync(fileUrl);
        var staff = await (await _factory.CreateClientWithRoleAsync("Staff"))
            .GetAsync($"/api/vehicle-verifications/{(await LatestAsync(client, vehicleId)).VerificationId}/files/{document.DocumentId}");

        own.StatusCode.Should().Be(HttpStatusCode.OK);
        own.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await own.Content.ReadAsByteArrayAsync()).Should().StartWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        stranger.StatusCode.Should().Be(HttpStatusCode.NotFound);
        staff.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Uploading_While_The_Check_Runs_Should_Be_Refused()
    {
        var (client, vehicleId) = await CustomerWithVehicleAsync();
        await UploadAllAsync(client, vehicleId, CertificateText, CardText);
        var context = _factory.GetRequiredService<PmsDbContext>();
        var draft = await context.VehicleVerifications.SingleAsync();
        draft.Status = VehicleVerificationStatus.Processing;
        await context.SaveChangesAsync();

        var response = await client.PostAsync($"{DocumentsUrl(vehicleId)}/CitizenId/Front", FileContent(Png(CardText)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("being checked");
    }
}
