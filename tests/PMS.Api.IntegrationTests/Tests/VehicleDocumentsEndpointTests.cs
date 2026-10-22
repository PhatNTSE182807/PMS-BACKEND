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
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

/// <summary>
/// Upload of the registration certificate (front) and Citizen ID (front and back), submission to the
/// Staff queue, and the Staff decision that approves or rejects the new vehicle.
/// </summary>
public class VehicleDocumentsEndpointTests : IDisposable
{
    private readonly ApiApplicationFactory<Program> _factory = new();

    private const string OwnCitizenId = "079204006789";

    public void Dispose() => _factory.Dispose();

    private static byte[] Png(string marker = "") =>
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }
            .Concat(Encoding.UTF8.GetBytes(marker))
            .ToArray();

    private static MultipartFormDataContent FileContent(byte[] bytes, string fileName = "photo.png", string contentType = "image/png")
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { file, "file", fileName } };
    }

    /// <summary>A customer with Citizen ID 079204006789 and a Porsche Taycan 51K-992.88.</summary>
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

        // The account data Staff compare the papers with
        var customer = await context.Vehicles.Where(v => v.Id == vehicle.VehicleId).Select(v => v.Customer).SingleAsync();
        customer.FullName = "Nguyễn Tấn Phát";
        customer.CitizenId = OwnCitizenId;
        await context.SaveChangesAsync();

        return (client, vehicle.VehicleId);
    }

    private static string DocumentsUrl(Guid vehicleId) => $"/api/users/profile/vehicles/{vehicleId}/documents";

    /// <summary>Uploads every required file: certificate front, Citizen ID front and back.</summary>
    private static async Task UploadAllAsync(HttpClient client, Guid vehicleId)
    {
        foreach (var (type, side) in VehicleDocumentKinds.Required)
        {
            var response = await client.PostAsync($"{DocumentsUrl(vehicleId)}/{type}/{side}", FileContent(Png($"{type}-{side}")));
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

    private static async Task<VehicleResponseModel> VehicleAsync(HttpClient client, Guid vehicleId)
    {
        var response = await client.GetAsync("/api/users/profile/vehicles");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await ResponseHelper.GetApiResultAsync<VehicleResponseModel[]>(response)).Result.Single(v => v.VehicleId == vehicleId);
    }

    private static Task<HttpResponseMessage> ReviewAsync(HttpClient staff, Guid verificationId, bool approve, string note = null) =>
        staff.PostAsync($"/api/vehicle-verifications/{verificationId}/review",
            new JsonContent(new ReviewVehicleVerificationModel { Approve = approve, Note = note }));

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
        verification.MissingDocuments.Should().BeEquivalentTo("RegistrationCertificate/Front", "CitizenId/Front");
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
    [InlineData("RegistrationCertificate", "Back")]
    public async Task Upload_Should_Reject_Unknown_Or_Unneeded_Papers(string type, string side)
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
    public async Task Submit_Should_Need_Every_Paper()
    {
        var (client, vehicleId) = await CustomerWithVehicleAsync();
        await client.PostAsync($"{DocumentsUrl(vehicleId)}/RegistrationCertificate/Front", FileContent(Png()));

        var response = await client.PostAsync($"{DocumentsUrl(vehicleId)}/submit", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("CitizenId/Front");
    }

    [Fact]
    public async Task New_Vehicle_Should_Have_No_Papers_Until_They_Are_Submitted()
    {
        var (client, vehicleId) = await CustomerWithVehicleAsync();

        (await VehicleAsync(client, vehicleId)).VerificationStatus.Should().BeNull();
        (await LatestAsync(client, vehicleId)).Should().BeNull();
    }

    [Fact]
    public async Task Submitted_Papers_Should_Wait_For_Staff_Who_Approve_The_Vehicle()
    {
        var (client, vehicleId) = await CustomerWithVehicleAsync();
        await UploadAllAsync(client, vehicleId);
        var verification = await SubmitAsync(client, vehicleId);

        // No automatic reading: the check goes straight to the Staff queue
        verification.Status.Should().Be(VehicleVerificationStatus.NeedsReview);
        (await VehicleAsync(client, vehicleId)).VerificationStatus.Should().Be(VehicleVerificationStatus.NeedsReview);

        // Customers cannot review; Staff see it in their queue with the account data to compare
        (await client.GetAsync("/api/vehicle-verifications")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var staff = await _factory.CreateClientWithRoleAsync("Staff");
        var queue = await ResponseHelper.GetApiResultAsync<VehicleVerificationReviewModel[]>(await staff.GetAsync("/api/vehicle-verifications"));
        var item = queue.Result.Should().ContainSingle(v => v.VerificationId == verification.VerificationId).Subject;
        item.CustomerCitizenId.Should().Be(OwnCitizenId);
        item.Documents.Should().HaveCount(3);

        // A rejection needs a reason; an approval decides the check
        (await ReviewAsync(staff, verification.VerificationId, approve: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReviewAsync(staff, verification.VerificationId, approve: true, "Papers match.")).StatusCode.Should().Be(HttpStatusCode.OK);

        var decided = await LatestAsync(client, vehicleId);
        decided.Status.Should().Be(VehicleVerificationStatus.Approved);
        decided.ReviewNote.Should().Be("Papers match.");
        decided.FilesDeleteAt.Should().BeCloseTo(decided.DecidedAt!.Value.AddDays(30), TimeSpan.FromSeconds(1));
        (await VehicleAsync(client, vehicleId)).VerificationStatus.Should().Be(VehicleVerificationStatus.Approved);

        // An approved vehicle keeps its papers
        var again = await client.PostAsync($"{DocumentsUrl(vehicleId)}/CitizenId/Front", FileContent(Png()));
        again.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rejected_Vehicle_Can_Send_New_Papers()
    {
        var (client, vehicleId) = await CustomerWithVehicleAsync();
        await UploadAllAsync(client, vehicleId);
        var first = await SubmitAsync(client, vehicleId);
        var staff = await _factory.CreateClientWithRoleAsync("Staff");

        (await ReviewAsync(staff, first.VerificationId, approve: false, "The certificate photo is blurry.")).StatusCode.Should().Be(HttpStatusCode.OK);

        var vehicle = await VehicleAsync(client, vehicleId);
        vehicle.VerificationStatus.Should().Be(VehicleVerificationStatus.Rejected);
        vehicle.VerificationNote.Should().Be("The certificate photo is blurry.");

        // New photos start a new check, which goes back to the queue
        await UploadAllAsync(client, vehicleId);
        var second = await SubmitAsync(client, vehicleId);
        second.VerificationId.Should().NotBe(first.VerificationId);
        second.Status.Should().Be(VehicleVerificationStatus.NeedsReview);
    }

    [Fact]
    public async Task Replacing_A_Side_Should_Keep_One_File_Per_Side()
    {
        var (client, vehicleId) = await CustomerWithVehicleAsync();
        await UploadAllAsync(client, vehicleId);
        await client.PostAsync($"{DocumentsUrl(vehicleId)}/RegistrationCertificate/Front", FileContent(Png("sharper")));

        var draft = await LatestAsync(client, vehicleId);

        draft.Documents.Should().HaveCount(3);
        draft.MissingDocuments.Should().BeEmpty();
    }

    [Fact]
    public async Task Owner_And_Staff_Can_Download_A_Photo_But_Others_Cannot()
    {
        var (client, vehicleId) = await CustomerWithVehicleAsync();
        await UploadAllAsync(client, vehicleId);
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
    public async Task Uploading_While_Waiting_For_Staff_Should_Be_Refused()
    {
        var (client, vehicleId) = await CustomerWithVehicleAsync();
        await UploadAllAsync(client, vehicleId);
        await SubmitAsync(client, vehicleId);

        var response = await client.PostAsync($"{DocumentsUrl(vehicleId)}/CitizenId/Front", FileContent(Png()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("waiting for Staff review");
    }
}
