using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

[CollectionDefinition("Work scheduling API", DisableParallelization = true)]
public class WorkSchedulingApiCollection { }

// Each factory owns a Hangfire server but Hangfire also has process-wide storage/logger state.
// Dispose these test hosts outside other API collections to avoid tearing down their shared logger.
[Collection("Work scheduling API")]
public class WorkSchedulingEndpointTests
{
    [Theory]
    [InlineData("Staff")]
    [InlineData("Admin")]
    [InlineData("User")]
    public async Task Manager_Endpoints_Reject_Other_Roles(string role)
    {
        using var factory = new ApiApplicationFactory<Program>();
        using var client = await factory.CreateClientWithRoleAsync(role);
        var response = await client.GetAsync("/api/manager/lots/1/work-scheduling/rosters");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anonymous_Cannot_Read_Schedule()
    {
        using var factory = new ApiApplicationFactory<Program>();
        using var client = factory.CreateUnauthorizedClient();
        (await client.GetAsync("/api/staff/work-schedule?from=2026-11-16&to=2026-11-22")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Http_Flow_Protects_Drafts_Records_Changes_And_Rejects_Stale_Revision()
    {
        using var factory = new ApiApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        var manager = new ApplicationUser { Id = Guid.NewGuid().ToString(), UserName = "test-manager", Role = "Manager", IsActive = true };
        var staff = new ApplicationUser { Id = Guid.NewGuid().ToString(), UserName = "test-staff", Role = "Staff", IsActive = true };
        identity.Users.AddRange(manager, staff);
        await identity.SaveChangesAsync();
        var lot = new ParkingLot { Code = "WORK-TEST", Name = "Test lot", Address = "Test", Status = "Active", ManagerUserId = manager.Id };
        db.ParkingLots.Add(lot); await db.SaveChangesAsync();
        var employee = new Employee { UserId = staff.Id, EmployeeCode = "WORK-EMP", FullName = "Test Staff", ParkingLotId = lot.Id, Position = "Attendant", Status = "Active" };
        db.Employees.Add(employee); await db.SaveChangesAsync();
        HttpClient Client(ApplicationUser user)
        {
            var c = factory.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", JwtHelper.GenerateToken(user, factory.Services.GetRequiredService<IConfiguration>()));
            return c;
        }
        using var managerClient = Client(manager); using var staffClient = Client(staff);
        var root = $"/api/manager/lots/{lot.Id}/work-scheduling";
        var templateResponse = await managerClient.PostAsJsonAsync($"{root}/templates", new { name = "Night", startTime = "22:00:00", endTime = "06:00:00", minimumEntryStaff = 2, isActive = true });
        templateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var template = (await ResponseHelper.GetApiResultAsync<WorkShiftTemplate>(templateResponse)).Result;
        var rosterResponse = await managerClient.PostAsJsonAsync($"{root}/rosters", new { name = "Week", fromDate = "2026-11-16", toDate = "2026-11-22" });
        rosterResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var r = (await ResponseHelper.GetApiResultAsync<DutyRosterModel>(rosterResponse)).Result;
        async Task<DutyRosterModel> Post(string path, object body)
        {
            var response = await managerClient.PostAsJsonAsync(path, body);
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return (await ResponseHelper.GetApiResultAsync<DutyRosterModel>(response)).Result;
        }
        r = await Post($"{root}/rosters/{r.Id}/shifts", new { revision = r.Revision, workShiftTemplateId = template.Id, workDate = "2026-11-16" });
        r = await Post($"{root}/rosters/{r.Id}/shifts/{r.Shifts.Single().Id}/assignments", new { revision = r.Revision, employeeId = employee.Id, position = "Entry" });
        const string mine = "/api/staff/work-schedule?from=2026-11-17&to=2026-11-17";
        var hidden = await ResponseHelper.GetApiResultAsync<List<DutyRosterModel>>(await staffClient.GetAsync(mine));
        hidden.Result.Should().BeEmpty();
        r = await Post($"{root}/rosters/{r.Id}/publish", new { revision = r.Revision });
        r.Shortages.Should().ContainSingle();
        var visible = await ResponseHelper.GetApiResultAsync<List<DutyRosterModel>>(await staffClient.GetAsync(mine));
        visible.Result.Single().Shifts.Single().Assignments.Should().ContainSingle(a => a.EmployeeId == employee.Id);
        var shift = r.Shifts.Single();
        var assignment = shift.Assignments.Single();
        var updatePath = $"{root}/rosters/{r.Id}/shifts/{shift.Id}/assignments/{assignment.Id}";
        (await managerClient.PutAsJsonAsync(updatePath, new { revision = r.Revision, employeeId = employee.Id, position = "Counter" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await managerClient.PutAsJsonAsync(updatePath, new { revision = r.Revision - 1, employeeId = employee.Id, position = "Counter", reason = "Move to counter" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var changed = await managerClient.PutAsJsonAsync(updatePath, new { revision = r.Revision, employeeId = employee.Id, position = "Counter", reason = "Move to counter" });
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        var history = await ResponseHelper.GetApiResultAsync<List<DutyRosterChangeLog>>(await managerClient.GetAsync($"{root}/rosters/{r.Id}/history"));
        history.Result.First().Reason.Should().Be("Move to counter");
        var current = (await ResponseHelper.GetApiResultAsync<DutyRosterModel>(changed)).Result;
        var available = await managerClient.GetAsync($"{root}/rosters/{r.Id}/eligible-staff?shiftId={shift.Id}&excludeAssignmentId={assignment.Id}");
        available.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ResponseHelper.GetApiResultAsync<List<RosterStaffModel>>(available)).Result.Should().ContainSingle(e => e.EmployeeId == employee.Id);
        current = await Post($"{root}/rosters/{r.Id}/calendar-assignments", new {
            revision = current.Revision, workDate = "2026-11-18", workShiftTemplateId = template.Id,
            employeeId = employee.Id, position = "Entry", notes = "Gate B task", reason = "Add coverage" });
        current.Shifts.Should().HaveCount(2);
        current.Shifts.Last().Assignments.Single().Notes.Should().Be("Gate B task");
        (await staffClient.GetAsync($"{root}/rosters/{r.Id}/eligible-staff?shiftId={shift.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // A valid Manager token still cannot access another Manager's lot.
        using var other = await factory.CreateClientWithRoleAsync("Manager");
        (await other.GetAsync($"{root}/rosters")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await staffClient.GetAsync($"{root}/rosters/{r.Id}/history")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var groupResponse = await managerClient.PostAsJsonAsync($"{root}/rotation-groups", new { name = "HTTP A", offsetDays = 0, members = new[] { new { employeeId = employee.Id, position = "Entry" } } });
        groupResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var group = (await ResponseHelper.GetApiResultAsync<RotationGroupModel>(groupResponse)).Result;
        var cycleResponse = await managerClient.PostAsJsonAsync($"{root}/rotation-cycles", new { name = "HTTP cycle", steps = new[] { new { templateId = template.Id, days = 1 } } });
        cycleResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var cycle = (await ResponseHelper.GetApiResultAsync<RotationCycleModel>(cycleResponse)).Result;
        var generated = await Post($"{root}/rosters", new { name = "Generated week", fromDate = "2026-11-23", toDate = "2026-11-25" });
        var generation = new RotationGenerationInput { Revision = generated.Revision, FromDate = generated.FromDate, ToDate = generated.ToDate, CycleStartDate = generated.FromDate, GroupIds = [group.Id], CycleId = cycle.Id };
        var previewUrl = $"{root}/rosters/{generated.Id}/rotation-preview";
        (await staffClient.PostAsJsonAsync(previewUrl, generation)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await other.PostAsJsonAsync(previewUrl, generation)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var previewResponse = await managerClient.PostAsJsonAsync(previewUrl, generation);
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = (await ResponseHelper.GetApiResultAsync<RotationPreviewModel>(previewResponse)).Result;
        preview.ProposedAssignments.Should().Be(3); preview.MinimumRestHours.Should().Be(1); preview.MaximumWeeklyHours.Should().Be(48);
        preview.LeaveSourceAvailable.Should().BeTrue();
        generation.PreviewToken = preview.Token;
        generated = await Post($"{root}/rosters/{generated.Id}/rotation-apply", generation);
        generated.Shifts.SelectMany(s => s.Assignments).Should().HaveCount(3);
        (await managerClient.PostAsJsonAsync($"{root}/rosters/{generated.Id}/rotation-apply", generation)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var staffRange = "/api/staff/work-schedule?from=2026-11-23&to=2026-11-25";
        (await ResponseHelper.GetApiResultAsync<List<DutyRosterModel>>(await staffClient.GetAsync(staffRange))).Result.Should().BeEmpty();
        generated = await Post($"{root}/rosters/{generated.Id}/publish", new { revision = generated.Revision });
        var personal = (await ResponseHelper.GetApiResultAsync<List<DutyRosterModel>>(await staffClient.GetAsync(staffRange))).Result;
        personal.Should().ContainSingle(); personal.Single().RotationConfigurationJson.Should().BeNull();
        generation.Revision = generated.Revision;
        (await managerClient.PostAsJsonAsync(previewUrl, generation)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
