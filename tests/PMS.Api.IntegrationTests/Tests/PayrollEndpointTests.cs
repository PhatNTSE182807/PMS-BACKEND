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
public class PayrollEndpointTests
{
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Factory : ApiApplicationFactory<Program>
    {
        public Clock Clock = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder) { base.ConfigureWebHost(builder); builder.ConfigureTestServices(services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock); }); }
    }
    [Fact]
    public async Task Payroll_Http_Flow_Uses_Locked_Evidence_Enforces_Roles_And_Preserves_Paid_Sources()
    {
        using var factory = new Factory(); using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>(); var identity = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        var manager = new ApplicationUser { Id = Guid.NewGuid().ToString(), Role = "Manager", IsActive = true };
        var staff = new ApplicationUser { Id = Guid.NewGuid().ToString(), Role = "Staff", IsActive = true };
        identity.Users.AddRange(manager, staff); await identity.SaveChangesAsync();
        var lot = new ParkingLot { Name = "Payroll scope", Code = "PAY-LOT", Address = "Test", Status = "Active", ManagerUserId = manager.Id };
        db.ParkingLots.Add(lot); await db.SaveChangesAsync();
        var employee = new Employee { UserId = staff.Id, FullName = "Payroll Staff fixture", EmployeeCode = "PAY-STAFF", ParkingLotId = lot.Id, Status = "Active", Position = "Staff" };
        db.Employees.Add(employee); await db.SaveChangesAsync();
        HttpClient Client(ApplicationUser user) { var c = factory.CreateClient(); c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", JwtHelper.GenerateToken(user, factory.Services.GetRequiredService<IConfiguration>())); return c; }
        using var m = Client(manager); using var owner = Client(staff); using var admin = await factory.CreateClientWithRoleAsync("Admin");
        var root = $"/api/manager/lots/{lot.Id}/work-scheduling";
        var template = (await ResponseHelper.GetApiResultAsync<WorkShiftTemplate>(await m.PostAsJsonAsync($"{root}/templates", new { name = "Salary source day", startTime = "08:00:00", endTime = "16:00:00", isActive = true }))).Result;
        var roster = (await ResponseHelper.GetApiResultAsync<DutyRosterModel>(await m.PostAsJsonAsync($"{root}/rosters", new { name = "Source roster", fromDate = "2026-10-30", toDate = "2026-10-30" }))).Result;
        roster = (await ResponseHelper.GetApiResultAsync<DutyRosterModel>(await m.PostAsJsonAsync($"{root}/rosters/{roster.Id}/calendar-assignments", new { revision = roster.Revision, workDate = "2026-10-30", workShiftTemplateId = template.Id, employeeId = employee.Id }))).Result;
        roster = (await ResponseHelper.GetApiResultAsync<DutyRosterModel>(await m.PostAsJsonAsync($"{root}/rosters/{roster.Id}/publish", new { revision = roster.Revision }))).Result;
        var assignmentId = roster.Shifts.Single().Assignments.Single().Id; var attendanceUrl = $"/api/work-scheduling/assignments/{assignmentId}/attendance";
        factory.Clock.Now = new(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);
        (await owner.PostAsJsonAsync($"{attendanceUrl}/start", new { revision = 0, rosterRevision = roster.Revision })).StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Clock.Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
        (await owner.PostAsJsonAsync($"{attendanceUrl}/end", new { revision = 1, rosterRevision = roster.Revision })).StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Clock.Now = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        var period = (await ResponseHelper.GetApiResultAsync<PeriodModel>(await admin.PostAsJsonAsync("/api/work-scheduling/attendance-periods", new { name = "Payroll October", month = "2026-10-01", lotIds = new[] { lot.Id } }))).Result;
        (await admin.PostAsJsonAsync($"/api/work-scheduling/attendance-periods/{period.Id}/lock", new { revision = 1, acknowledgeUnconfirmed = true, reason = "Verified full month" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = new { userId = staff.Id, role = "Staff", effectiveFrom = "2026-01-01", hourlyRate = 100, nightStart = "22:00:00", nightEnd = "06:00:00", nightFactor = 1.5m, holidayFactor = 2, overtimeFactor = 1.25m, currency = "VND", moneyDecimals = 2, roundingMode = "AwayFromZero", reason = "Explicit arithmetic fixture, not a production default" };
        (await m.PostAsJsonAsync("/api/payroll/profiles", profile)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PostAsJsonAsync("/api/payroll/profiles", profile)).StatusCode.Should().Be(HttpStatusCode.OK);
        var generate = new { parkingLotId = lot.Id, payMonth = "2026-10-01", role = "Staff", reason = "8 hours × 100 = 800" };
        (await owner.PostAsJsonAsync("/api/payroll/generate", generate)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var slip = (await ResponseHelper.GetApiResultAsync<PayrollSlip[]>(await admin.PostAsJsonAsync("/api/payroll/generate", generate))).Result.Single();
        slip.Total.Should().Be(800); var url = $"/api/payroll/slips/{slip.Id}";
        (await m.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owner.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PostAsJsonAsync($"{url}/approve", new { revision = 1, reason = "Arithmetic verified" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await m.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = (await ResponseHelper.GetApiResultAsync<PayrollSlipDetail>(await owner.GetAsync(url))).Result;
        detail.Lines.Single().AssignmentId.Should().Be(assignmentId);
        var source = await owner.GetAsync($"{url}/lines/{detail.Lines.Single().Id}/source");
        source.StatusCode.Should().Be(HttpStatusCode.OK); (await source.Content.ReadAsStringAsync()).Should().Contain("Salary source day").And.Contain("\"frozen\":true");
        using var outsider = await factory.CreateClientWithRoleAsync("Staff");
        (await outsider.GetAsync($"{url}/lines/{detail.Lines.Single().Id}/source")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        factory.Clock.Now = factory.Clock.Now.AddSeconds(1);
        (await admin.PostAsJsonAsync($"{url}/paid", new { revision = 2, reason = "External payment completed", paidAt = factory.Clock.Now, transactionReference = "HTTP-TRANSFER-001" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PostAsJsonAsync("/api/payroll/generate", generate)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await m.PostAsJsonAsync($"{url}/paid", new { revision = 3, reason = "Wrong role" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await outsider.PostAsJsonAsync($"{url}/complaints", new { reason = "Not mine" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var complaint = (await ResponseHelper.GetApiResultAsync<PayrollComplaint>(await owner.PostAsJsonAsync($"{url}/complaints", new { reason = "Please inspect my original hours" }))).Result;
        (await admin.PostAsJsonAsync($"/api/payroll/complaints/{complaint.Id}/resolve", new { revision = 1, action = "Accept", reason = "Agreed correction", adjustment = new { payMonth = "2026-11-01", quantity = 1, unitRate = 25, reason = "Source evidence correction" } })).StatusCode.Should().Be(HttpStatusCode.OK);
        detail = (await ResponseHelper.GetApiResultAsync<PayrollSlipDetail>(await owner.GetAsync(url))).Result;
        detail.Slip.Total.Should().Be(800); detail.Slip.Status.Should().Be("Paid"); detail.Complaints.Single().Status.Should().Be("Accepted");
    }
}
