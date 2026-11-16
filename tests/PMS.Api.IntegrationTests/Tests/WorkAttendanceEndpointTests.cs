using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PMS.API;
using PMS.Api.IntegrationTests.Common;
using PMS.Api.IntegrationTests.Helpers;
using PMS.Application.Helpers;
using PMS.Application.Models.WorkScheduling;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Api.IntegrationTests.Tests;

[Collection("Work scheduling API")]
public class WorkAttendanceEndpointTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Factory : ApiApplicationFactory<Program>
    {
        public ManualClock Clock = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock); });
        }
    }

    [Fact]
    public async Task Attendance_Http_Flow_Updates_The_Same_Grid_And_Enforces_Owner_Manager_And_Revisions()
    {
        using var factory = new Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        var manager = new ApplicationUser { Id = Guid.NewGuid().ToString(), Role = "Manager", IsActive = true };
        var staff = new ApplicationUser { Id = Guid.NewGuid().ToString(), Role = "Staff", IsActive = true };
        identity.Users.AddRange(manager, staff); await identity.SaveChangesAsync();
        var lot = new ParkingLot { Code = "ATT-LOT", Name = "Attendance lot", Address = "Test", Status = "Active", ManagerUserId = manager.Id };
        db.ParkingLots.Add(lot); await db.SaveChangesAsync();
        var employee = new Employee { UserId = staff.Id, EmployeeCode = "ATT-STAFF", FullName = "Attendance Staff", ParkingLotId = lot.Id, Position = "Attendant", Status = "Active" };
        db.Employees.Add(employee); await db.SaveChangesAsync();
        HttpClient Client(ApplicationUser user)
        {
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", JwtHelper.GenerateToken(user, factory.Services.GetRequiredService<IConfiguration>()));
            return client;
        }
        using var managerClient = Client(manager); using var staffClient = Client(staff);
        var root = $"/api/manager/lots/{lot.Id}/work-scheduling";
        var template = (await ResponseHelper.GetApiResultAsync<WorkShiftTemplate>(await managerClient.PostAsJsonAsync($"{root}/templates",
            new { name = "Morning", startTime = "08:00:00", endTime = "16:00:00", isActive = true }))).Result;
        var r = (await ResponseHelper.GetApiResultAsync<DutyRosterModel>(await managerClient.PostAsJsonAsync($"{root}/rosters",
            new { name = "Attendance roster", fromDate = "2026-11-16", toDate = "2026-11-17" }))).Result;
        r = (await ResponseHelper.GetApiResultAsync<DutyRosterModel>(await managerClient.PostAsJsonAsync($"{root}/rosters/{r.Id}/calendar-assignments",
            new { revision = r.Revision, workDate = "2026-11-16", workShiftTemplateId = template.Id, employeeId = employee.Id, position = "Entry" }))).Result;
        r = (await ResponseHelper.GetApiResultAsync<DutyRosterModel>(await managerClient.PostAsJsonAsync($"{root}/rosters/{r.Id}/publish", new { revision = r.Revision }))).Result;
        var id = r.Shifts.Single().Assignments.Single().Id;
        var url = $"/api/work-scheduling/assignments/{id}/attendance";
        (await managerClient.PostAsJsonAsync($"{url}/start", new { revision = 0, rosterRevision = r.Revision })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await staffClient.PutAsJsonAsync(url, new { revision = 0, rosterRevision = r.Revision, reason = "Not permitted" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await staffClient.PostAsJsonAsync($"{url}/start", new { revision = 0, rosterRevision = r.Revision })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.Clock.Now = new DateTimeOffset(2026, 11, 16, 0, 30, 0, TimeSpan.Zero);
        var response = await staffClient.PostAsJsonAsync($"{url}/start", new { revision = 0, rosterRevision = r.Revision });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = (await ResponseHelper.GetApiResultAsync<AttendanceDetailModel>(response)).Result;
        detail.Attendance.Status.Should().Be("Working");
        var grid = (await ResponseHelper.GetApiResultAsync<DutyRosterModel[]>(await managerClient.GetAsync($"{root}/rosters"))).Result;
        grid.Single().Shifts.Single().Assignments.Single().Attendance.Status.Should().Be("Working");
        factory.Clock.Now = new DateTimeOffset(2026, 11, 16, 1, 30, 0, TimeSpan.Zero);
        var correction = new { revision = 1, rosterRevision = r.Revision, checkInAt = "2026-11-16T00:30:00Z", checkOutAt = "2026-11-16T01:25:00Z" };
        (await managerClient.PutAsJsonAsync(url, correction)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var fixedResponse = await managerClient.PutAsJsonAsync(url, new { revision = 1, rosterRevision = r.Revision, checkInAt = "2026-11-16T00:30:00Z", checkOutAt = "2026-11-16T01:25:00Z", reason = "Verified handover" });
        fixedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        detail = (await ResponseHelper.GetApiResultAsync<AttendanceDetailModel>(await staffClient.GetAsync(url))).Result;
        detail.Attendance.Status.Should().Be("Completed"); detail.History.Should().HaveCount(2);
        detail.History.First().Reason.Should().Be("Verified handover");
        (await staffClient.PostAsJsonAsync($"{url}/end", new { revision = 1, rosterRevision = r.Revision })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var other = await factory.CreateClientWithRoleAsync("Manager");
        (await other.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await other.PutAsJsonAsync(url, new { revision = 2, rosterRevision = r.Revision, reason = "Wrong lot" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var otherStaff = await factory.CreateClientWithRoleAsync("Staff");
        (await otherStaff.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        factory.Clock.Now = new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
        using var admin = await factory.CreateClientWithRoleAsync("Admin");
        const string periodsUrl = "/api/work-scheduling/attendance-periods";
        (await staffClient.GetAsync($"{periodsUrl}?lotId={lot.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await managerClient.PostAsJsonAsync(periodsUrl, new { name = "Forbidden", fromDate = "2026-11-16", toDate = "2026-11-16", lotIds = new[] { lot.Id } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var period = (await ResponseHelper.GetApiResultAsync<PeriodModel>(await admin.PostAsJsonAsync(periodsUrl,
            new { name = "Explicit attendance day", fromDate = "2026-11-16", toDate = "2026-11-16", lotIds = new[] { lot.Id } }))).Result;
        var reviewUrl = $"{periodsUrl}/{period.Id}/lots/{lot.Id}";
        (await admin.PutAsJsonAsync($"{periodsUrl}/manager-days/lots/{lot.Id}", new { managerUserId = manager.Id, workDate = "2026-11-16", kind = "Work", reason = "Admin verified Manager day" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await other.GetAsync(reviewUrl)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var overtime = await managerClient.PostAsJsonAsync($"{url}/overtime", new { revision = 2, rosterRevision = r.Revision, approved = false, reason = "Early time not approved" });
        overtime.StatusCode.Should().Be(HttpStatusCode.OK);
        (await managerClient.PostAsJsonAsync($"{url}/overtime", new { revision = 2, rosterRevision = r.Revision, approved = true, reason = "Stale decision request" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var review = (await ResponseHelper.GetApiResultAsync<AttendancePeriodReview>(await managerClient.GetAsync(reviewUrl))).Result;
        review.Warnings.Should().BeEmpty(); review.Lot.Status.Should().Be("Open");
        (await admin.PostAsJsonAsync($"{reviewUrl}/confirm", new { revision = review.Lot.Revision, reason = "Wrong role" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await managerClient.PostAsJsonAsync($"{reviewUrl}/confirm", new { revision = review.Lot.Revision, reason = "Verified all staff" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var existing = r.Shifts.Single().Assignments.Single();
        (await managerClient.PutAsJsonAsync($"{root}/rosters/{r.Id}/shifts/{r.Shifts.Single().Id}/assignments/{id}",
            new { revision = r.Revision, employeeId = existing.EmployeeId, position = "Entry", notes = "Changed after confirmation", reason = "Handover correction" })).StatusCode.Should().Be(HttpStatusCode.OK);
        review = (await ResponseHelper.GetApiResultAsync<AttendancePeriodReview>(await managerClient.GetAsync(reviewUrl))).Result;
        review.Lot.Status.Should().Be("Open");
        (await managerClient.PostAsJsonAsync($"{reviewUrl}/confirm", new { revision = review.Lot.Revision, reason = "Verified again" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await managerClient.PostAsJsonAsync($"{periodsUrl}/{period.Id}/lock", new { revision = 1, reason = "Wrong role" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PostAsJsonAsync($"{periodsUrl}/{period.Id}/lock", new { revision = 1, reason = "Final audit completed" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var lockedReviewResponse = await admin.GetAsync(reviewUrl);
        (await lockedReviewResponse.Content.ReadAsStringAsync()).Should().Contain("\"managerName\"");
        review = (await ResponseHelper.GetApiResultAsync<AttendancePeriodReview>(lockedReviewResponse)).Result;
        review.ManagerDays.Should().ContainSingle(d => d.Kind == "Work");
        (await managerClient.PutAsJsonAsync(url, new { revision = 2, rosterRevision = r.Revision + 1, checkInAt = "2026-11-16T01:00:00Z", checkOutAt = "2026-11-16T01:25:00Z", reason = "Locked edit" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PutAsJsonAsync($"{periodsUrl}/manager-days/lots/{lot.Id}", new { managerUserId = manager.Id, workDate = "2026-11-16", kind = "Work", reason = "Locked day" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        detail = (await ResponseHelper.GetApiResultAsync<AttendanceDetailModel>(await staffClient.GetAsync(url))).Result;
        detail.PayReview.Locked.Should().BeTrue(); detail.Attendance.CanEnd.Should().BeFalse();
        grid = (await ResponseHelper.GetApiResultAsync<DutyRosterModel[]>(await managerClient.GetAsync($"{root}/rosters"))).Result;
        grid.Single().Shifts.Single().AttendanceLocked.Should().BeTrue();
    }
}
