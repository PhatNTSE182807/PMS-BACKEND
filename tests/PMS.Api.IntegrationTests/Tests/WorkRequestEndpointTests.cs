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
public class WorkRequestEndpointTests
{
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero); }
    private sealed class Factory : ApiApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(new Clock()); });
        }
    }
    [Fact]
    public async Task Leave_Claim_And_Confirmation_Update_The_Same_Calendar_With_Scoped_Authorization()
    {
        using var factory = new Factory(); using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>(); var users = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        var manager = new ApplicationUser { Id = Guid.NewGuid().ToString(), Role = "Manager", IsActive = true };
        var owner = new ApplicationUser { Id = Guid.NewGuid().ToString(), Role = "Staff", IsActive = true };
        var replacement = new ApplicationUser { Id = Guid.NewGuid().ToString(), Role = "Staff", IsActive = true };
        users.Users.AddRange(manager, owner, replacement); await users.SaveChangesAsync();
        var lot = new ParkingLot { Name = "Requests", Code = "REQ", Address = "Test", Status = "Active", ManagerUserId = manager.Id };
        db.ParkingLots.Add(lot); await db.SaveChangesAsync();
        var a = new Employee { UserId = owner.Id, FullName = "Owner", EmployeeCode = "REQ-A", Status = "Active", Position = "Staff", ParkingLotId = lot.Id };
        var b = new Employee { UserId = replacement.Id, FullName = "Replacement", EmployeeCode = "REQ-B", Status = "Active", Position = "Staff", ParkingLotId = lot.Id };
        db.Employees.AddRange(a, b); await db.SaveChangesAsync();
        HttpClient Client(ApplicationUser user) { var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", JwtHelper.GenerateToken(user, factory.Services.GetRequiredService<IConfiguration>())); return client; }
        using var m = Client(manager); using var staff = Client(owner); using var peer = Client(replacement);
        var root = $"/api/manager/lots/{lot.Id}/work-scheduling";
        var template = (await ResponseHelper.GetApiResultAsync<WorkShiftTemplate>(await m.PostAsJsonAsync($"{root}/templates", new { name = "Night", startTime = "22:00:00", endTime = "06:00:00", minimumEntryStaff = 1, isActive = true }))).Result;
        var roster = (await ResponseHelper.GetApiResultAsync<DutyRosterModel>(await m.PostAsJsonAsync($"{root}/rosters", new { name = "Requests", fromDate = "2026-11-20", toDate = "2026-11-21" }))).Result;
        roster = (await ResponseHelper.GetApiResultAsync<DutyRosterModel>(await m.PostAsJsonAsync($"{root}/rosters/{roster.Id}/calendar-assignments", new { revision = roster.Revision, workDate = "2026-11-20", workShiftTemplateId = template.Id, employeeId = a.Id, position = "Entry" }))).Result;
        roster = (await ResponseHelper.GetApiResultAsync<DutyRosterModel>(await m.PostAsJsonAsync($"{root}/rosters/{roster.Id}/publish", new { revision = roster.Revision }))).Result;
        var original = roster.Shifts.Single().Assignments.Single().Id;
        var detailUrl = $"/api/work-scheduling/assignments/{original}/requests";
        (await peer.GetAsync(detailUrl)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var leaveResponse = await staff.PostAsJsonAsync("/api/work-scheduling/leave-requests", new { assignmentId = original, kind = "Planned", reason = "Private personal reason", proposedEmployeeId = b.Id });
        leaveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var leave = (await ResponseHelper.GetApiResultAsync<WorkLeaveRequest>(leaveResponse)).Result;
        var actionUrl = $"/api/work-scheduling/leave-requests/{leave.Id}/actions";
        (await peer.PostAsJsonAsync(actionUrl, new { revision = 1, action = "Approve", reason = "No authority" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await m.PostAsJsonAsync(actionUrl, new { revision = 1, action = "Approve", reason = "Approved" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var board = (await ResponseHelper.GetApiResultAsync<WorkRequestsModel>(await peer.GetAsync($"/api/work-scheduling/lots/{lot.Id}/requests?year=2026"))).Result;
        board.Leaves.Should().BeEmpty(); var slot = board.OpenShifts.Single();
        var slotUrl = $"/api/work-scheduling/open-shifts/{slot.Id}";
        (await peer.PostAsJsonAsync($"{slotUrl}/actions", new { revision = slot.Revision, action = "Claim" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var details = (await ResponseHelper.GetApiResultAsync<OpenShiftDetailModel>(await m.GetAsync(slotUrl))).Result;
        (await m.PostAsJsonAsync($"{slotUrl}/actions", new { revision = details.Slot.Revision, action = "Confirm", claimId = details.Claims.Single().Id, reason = "Confirmed replacement" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var own = (await ResponseHelper.GetApiResultAsync<DutyRosterModel[]>(await staff.GetAsync("/api/staff/work-schedule?from=2026-11-20&to=2026-11-21"))).Result.Single();
        own.Shifts.Single().Assignments.Should().BeEmpty(); own.Shifts.Single().Trail.Should().ContainSingle(t => t.Kind == "ApprovedLeave" && t.IsPaid);
        var replaced = (await ResponseHelper.GetApiResultAsync<DutyRosterModel[]>(await peer.GetAsync("/api/staff/work-schedule?from=2026-11-20&to=2026-11-21"))).Result.Single();
        replaced.Shifts.Single().Assignments.Should().ContainSingle(x => x.OriginalAssignmentId == original && x.EmployeeId == b.Id);
        (await peer.GetAsync("/api/admin/work-scheduling/leave-balances?year=2026")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await m.PostAsJsonAsync(actionUrl, new { revision = 1, action = "Approve", reason = "Duplicate" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
