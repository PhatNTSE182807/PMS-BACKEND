using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.WorkScheduling;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Operations;
public class WorkRotationTests
{
    private sealed class Leaves : IApprovedWorkLeaveSource
    {
        public bool Available = true;
        public List<ApprovedWorkLeave> Rows = [];
        public Task<ApprovedWorkLeaveResult> ReadAsync(int lotId, DateTime from, DateTime to) => Task.FromResult(new ApprovedWorkLeaveResult(Available, Rows.ToList()));
    }
    private sealed class World : IDisposable
    {
        public OperationsTestContext T;
        public DatabaseContext Identity;
        public WorkSchedulingService S;
        public Leaves Leave;
        public DutyRosterModel R;
        public RotationGroupModel Group;
        public RotationCycleModel Cycle;
        public List<WorkShiftTemplate> Templates;
        public int EmployeeId;
        public DateOnly Day = new(2026, 11, 16); // Monday, local time.
        public static async Task<World> Create(int days = 14, bool defaultCycle = true)
        {
            var t = await new OperationsTestContext().SeedAsync(); t.ActAs(t.ManagerUserId, "Manager");
            t.Clock.Now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
            var identity = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, t.Claims);
            identity.Users.Add(new ApplicationUser { Id = t.StaffUserId, Role = "Staff", IsActive = true }); await identity.SaveChangesAsync();
            var leave = new Leaves(); var s = new WorkSchedulingService(t.Db, identity, new LotScopeService(t.Db, t.Claims), t.Clock, approvedLeaveSource: leave);
            var templates = new List<WorkShiftTemplate>();
            foreach (var hour in new[] { 6, 14, 22 }) templates.Add(await s.SaveTemplateAsync(t.Lot.Id, null, new WorkShiftTemplateInput {
                Name = $"Shift {hour}", StartTime = new TimeOnly(hour, 0), EndTime = new TimeOnly((hour + 8) % 24, 0), MinimumStaff = 1 }));
            var employee = await t.Db.Employees.SingleAsync(e => e.UserId == t.StaffUserId);
            var group = await s.SaveRotationGroupAsync(t.Lot.Id, null, new RotationGroupInput { Name = "A", Members = [new(employee.Id)] });
            var cycle = await s.SaveRotationCycleAsync(t.Lot.Id, null, new RotationCycleInput { Name = "Default", Steps = defaultCycle
                ? [new(templates[0].Id, 2), new(templates[1].Id, 2), new(templates[2].Id, 2), new(null, 1)] : [new(templates[0].Id, 1)] });
            var day = new DateOnly(2026, 11, 16);
            var r = await s.CreateRosterAsync(t.Lot.Id, new CreateDutyRosterInput { Name = "Rotation", FromDate = day, ToDate = day.AddDays(days - 1) });
            return new World { T = t, Identity = identity, S = s, Leave = leave, R = r, Group = group, Cycle = cycle, Templates = templates, EmployeeId = employee.Id };
        }
        public RotationGenerationInput Input(bool replace = false) => new() { Revision = R.Revision, FromDate = R.FromDate, ToDate = R.ToDate,
            CycleStartDate = Day, GroupIds = [Group.Id], CycleId = Cycle.Id, ReplaceGenerated = replace };
        public async Task<RotationPreviewModel> Preview(RotationGenerationInput input = null) => await S.PreviewRotationAsync(T.Lot.Id, R.Id, input ?? Input());
        public async Task Save(RotationGenerationInput input = null)
        {
            input ??= Input(); input.PreviewToken = (await Preview(input)).Token; R = await S.ApplyRotationAsync(T.Lot.Id, R.Id, input);
        }
        public void Dispose() { T.Db.Dispose(); Identity.Dispose(); }
    }

    [Fact]
    public async Task Default_Cycle_Previews_Without_Writes_And_Saves_Into_Existing_Draft()
    {
        using var w = await World.Create(); var preview = await w.Preview();
        preview.ProposedAssignments.Should().Be(12); preview.Roster.Shortages.Should().NotBeEmpty();
        w.T.Db.DutyAssignments.Should().BeEmpty(); w.T.Db.DutyRosterShifts.Should().BeEmpty();
        await w.Save(); w.R.Id.Should().Be(1); w.R.Status.Should().Be("Draft"); w.R.Shifts.Should().HaveCount(42);
        w.R.Shifts.SelectMany(s => s.Assignments).Should().HaveCount(12);
        w.R.Shifts.Where(s => s.Assignments.Count > 0).Select(s => (s.WorkDate.DayNumber - w.Day.DayNumber, s.StartTime.Hour)).Should().Contain((4, 22));
        w.R.Shifts.Single(s => s.WorkDate == w.Day.AddDays(4) && s.Assignments.Count > 0).EndsAt.AddHours(7).Date.Should().Be(w.Day.AddDays(5).ToDateTime(TimeOnly.MinValue).Date);
        (await w.S.PublishAsync(w.T.Lot.Id, w.R.Id, new RosterMutationInput { Revision = w.R.Revision })).Status.Should().Be("Published");
        w.T.ActAs(w.T.StaffUserId, "Staff");
        var mine = await w.S.MyScheduleAsync(w.R.FromDate, w.R.ToDate);
        mine.SelectMany(r => r.Shifts).SelectMany(s => s.Assignments).Should().HaveCount(12);
    }

    [Fact]
    public async Task Regeneration_Append_Is_Idempotent_And_Repeated_Save_Is_Rejected()
    {
        using var w = await World.Create(7); await w.Save(); var input = w.Input(); var preview = await w.Preview(input);
        preview.ProposedAssignments.Should().Be(0); input.PreviewToken = preview.Token;
        w.R = await w.S.ApplyRotationAsync(w.T.Lot.Id, w.R.Id, input);
        w.T.Db.DutyAssignments.Count(a => !a.IsCancelled).Should().Be(6);
        await FluentActions.Invoking(() => w.S.ApplyRotationAsync(w.T.Lot.Id, w.R.Id, input)).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Replacement_Stays_In_Range_And_Preserves_Manual_Notes_And_Cancellation()
    {
        using var w = await World.Create(7); await w.Save();
        var first = w.R.Shifts.First(s => s.Assignments.Count > 0); var a = first.Assignments.Single();
        w.R = await w.S.SaveAssignmentAsync(w.T.Lot.Id, w.R.Id, first.Id, a.Id, new DutyAssignmentInput { Revision = w.R.Revision, EmployeeId = a.EmployeeId, Notes = "Keep this manual note" });
        var second = w.R.Shifts.First(s => s.WorkDate == w.Day.AddDays(1) && s.Assignments.Count > 0);
        w.R = await w.S.RemoveAssignmentAsync(w.T.Lot.Id, w.R.Id, second.Id, second.Assignments.Single().Id, new RosterMutationInput { Revision = w.R.Revision });
        var input = w.Input(true); input.ToDate = w.Day.AddDays(2); await w.Save(input);
        w.R.Shifts.SelectMany(s => s.Assignments).Should().Contain(x => x.Id == a.Id && x.Notes == "Keep this manual note" && x.IsManuallyEdited);
        w.R.Shifts.Where(s => s.WorkDate == w.Day.AddDays(1)).SelectMany(s => s.Assignments).Should().BeEmpty();
        w.R.Shifts.SelectMany(s => s.Assignments).Should().HaveCount(5);
    }

    [Fact]
    public async Task Published_And_Attendance_Rows_Are_Never_Replaced()
    {
        using var w = await World.Create(7); await w.Save();
        var a = w.R.Shifts.SelectMany(s => s.Assignments).First();
        w.T.Db.DutyAttendances.Add(new DutyAttendance { DutyAssignmentId = a.Id, Revision = 1 }); await w.T.Db.SaveChangesAsync();
        var preview = await w.Preview(w.Input(true)); preview.Changes.Should().Contain(x => x.AssignmentId == a.Id && x.Kind == "Keep");
        await w.Save(w.Input(true)); w.T.Db.DutyAssignments.Single(x => x.Id == a.Id).IsCancelled.Should().BeFalse();
        w.R = await w.S.PublishAsync(w.T.Lot.Id, w.R.Id, new RosterMutationInput { Revision = w.R.Revision });
        await FluentActions.Invoking(() => w.Preview()).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Stale_Preview_Is_Rejected_After_Group_Or_Leave_Changes()
    {
        using var w = await World.Create(7); var input = w.Input(); input.PreviewToken = (await w.Preview(input)).Token;
        w.Leave.Rows.Add(new(w.EmployeeId, w.Day.ToDateTime(TimeOnly.MinValue), w.Day.AddDays(1).ToDateTime(TimeOnly.MinValue)));
        await FluentActions.Invoking(() => w.S.ApplyRotationAsync(w.T.Lot.Id, w.R.Id, input)).Should().ThrowAsync<ConflictException>();
        w.T.Db.DutyAssignments.Should().BeEmpty();
        input.PreviewToken = (await w.Preview(input)).Token;
        await w.S.SaveRotationGroupAsync(w.T.Lot.Id, w.Group.Id, new RotationGroupInput { Name = "A", OffsetDays = 1, Revision = w.Group.Revision, Members = w.Group.Members.ToList() });
        await FluentActions.Invoking(() => w.S.ApplyRotationAsync(w.T.Lot.Id, w.R.Id, input)).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Approved_Leave_Excludes_Overnight_Intersection_And_Unavailable_Source_Is_Explicit()
    {
        using var w = await World.Create(7);
        // On day 4 Night ends day 5 at 06:00 local; the leave starts at midnight local day 5.
        var from = w.Day.AddDays(5).ToDateTime(TimeOnly.MinValue).AddHours(-7);
        w.Leave.Rows.Add(new(w.EmployeeId, from, from.AddHours(1)));
        var preview = await w.Preview(); preview.ProposedAssignments.Should().Be(5); preview.Issues.Should().Contain(x => x.Code == "ApprovedLeave");
        w.Leave.Rows.Clear(); w.Leave.Available = false;
        (await w.Preview()).Issues.Should().Contain(x => x.Code == "LeaveSourceUnavailable" && !x.Blocking);
    }

    [Fact]
    public async Task Weekly_Limit_Is_48_Hours_And_Does_Not_Assume_Offsets_Provide_Coverage()
    {
        using var w = await World.Create(7, false); var preview = await w.Preview();
        preview.ProposedAssignments.Should().Be(6); preview.Issues.Should().Contain(x => x.Code == "WeeklyHours");
        preview.Roster.Shortages.Should().Contain(x => x.WorkDate == w.Day.AddDays(6));
    }

    [Fact]
    public async Task Rest_Limit_Checks_Adjacent_Rosters_And_Exactly_60_Minutes_Is_Allowed()
    {
        using var w = await World.Create(1, false);
        var r = await w.S.CreateRosterAsync(w.T.Lot.Id, new CreateDutyRosterInput { Name = "Prior", FromDate = w.Day.AddDays(-1), ToDate = w.Day.AddDays(-1) });
        var t = await w.S.SaveTemplateAsync(w.T.Lot.Id, null, new WorkShiftTemplateInput { Name = "Prior night", StartTime = new TimeOnly(22, 0), EndTime = new TimeOnly(5, 1) });
        await w.S.AssignFromCalendarAsync(w.T.Lot.Id, r.Id, new CalendarAssignmentInput { Revision = r.Revision, EmployeeId = w.EmployeeId, WorkDate = w.Day.AddDays(-1), WorkShiftTemplateId = t.Id });
        (await w.Preview()).Issues.Should().Contain(x => x.Code == "MinimumRest");
        var shift = w.T.Db.DutyRosterShifts.Single(s => s.DutyRosterId == r.Id); shift.EndsAt = shift.EndsAt.AddMinutes(-1); await w.T.Db.SaveChangesAsync();
        (await w.Preview()).ProposedAssignments.Should().Be(1);
    }

    [Fact]
    public async Task Offset_Is_Deterministic()
    {
        using var w = await World.Create(7);
        await w.S.SaveRotationGroupAsync(w.T.Lot.Id, w.Group.Id, new RotationGroupInput { Name = "A", OffsetDays = 2, Revision = w.Group.Revision, Members = w.Group.Members.ToList() });
        var input = w.Input();
        var preview = await w.Preview(input);
        preview.Roster.Shifts.Single(s => s.WorkDate == w.Day && s.Assignments.Count > 0).StartTime.Hour.Should().Be(14);
        preview.Roster.Shifts.SelectMany(s => s.Assignments).Should().HaveCount(preview.ProposedAssignments);
        (await w.Preview(input)).Token.Should().Be(preview.Token);
    }

    [Theory]
    [InlineData("Staff", "staff-1")]
    [InlineData("Admin", "admin-1")]
    [InlineData("Manager", "other-manager")]
    public async Task Group_And_Generation_Require_The_Lot_Manager(string role, string user)
    {
        using var w = await World.Create(7); w.T.ActAs(user, role);
        await FluentActions.Invoking(() => w.Preview()).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => w.S.RotationGroupsAsync(w.T.Lot.Id)).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Published_Another_Roster_Is_Kept_And_Blocks_Overlapping_Proposals()
    {
        using var w = await World.Create(1, false);
        var r = await w.S.CreateRosterAsync(w.T.Lot.Id, new CreateDutyRosterInput { Name = "Existing", FromDate = w.Day, ToDate = w.Day });
        r = await w.S.AssignFromCalendarAsync(w.T.Lot.Id, r.Id, new CalendarAssignmentInput { Revision = r.Revision, EmployeeId = w.EmployeeId, WorkDate = w.Day, WorkShiftTemplateId = w.Templates[0].Id });
        r = await w.S.PublishAsync(w.T.Lot.Id, r.Id, new RosterMutationInput { Revision = r.Revision });
        var preview = await w.Preview(w.Input(true)); preview.ProposedAssignments.Should().Be(0);
        preview.Changes.Should().Contain(x => x.Kind == "Keep" && x.Detail.Contains("Published"));
        preview.Issues.Should().Contain(x => x.Code == "Overlap");
        await w.Save(w.Input(true));
        (await w.S.RostersAsync(w.T.Lot.Id)).Single(x => x.Id == r.Id).Revision.Should().Be(r.Revision);
    }

    [Fact]
    public async Task Sunday_Night_Hours_Are_Checked_Against_The_Following_Week()
    {
        using var w = await World.Create(6, false); await w.Save(); // 48 hours Monday to Saturday.
        var sunday = w.Day.AddDays(-1);
        var r = await w.S.CreateRosterAsync(w.T.Lot.Id, new CreateDutyRosterInput { Name = "Sunday", FromDate = sunday, ToDate = sunday });
        var cycle = await w.S.SaveRotationCycleAsync(w.T.Lot.Id, null, new RotationCycleInput { Name = "Night only", Steps = [new(w.Templates[2].Id, 1)] });
        var input = w.Input(); input.FromDate = sunday; input.ToDate = sunday; input.CycleStartDate = sunday; input.CycleId = cycle.Id; input.Revision = r.Revision;
        var preview = await w.S.PreviewRotationAsync(w.T.Lot.Id, r.Id, input);
        preview.ProposedAssignments.Should().Be(0); preview.Issues.Should().Contain(x => x.Code == "WeeklyHours" && x.Message.Contains("2026-11-16"));
    }

    [Fact]
    public async Task Long_Roster_Crosses_Year_Without_Changing_The_Roster_Period()
    {
        using var w = await World.Create(93); var preview = await w.Preview();
        preview.ProposedAssignments.Should().Be(80); preview.Roster.Shifts.Should().HaveCount(279);
        preview.Roster.FromDate.Should().Be(w.R.FromDate); preview.Roster.ToDate.Should().Be(w.R.ToDate);
        preview.Issues.Where(i => i.Blocking).Should().BeEmpty();
    }

    [Fact]
    public async Task Cycle_Anchor_After_The_Viewed_Range_Uses_Positive_Modulo()
    {
        using var w = await World.Create(7); var input = w.Input(); input.CycleStartDate = w.Day.AddDays(1);
        var preview = await w.Preview(input);
        preview.Roster.Shifts.Where(s => s.WorkDate == w.Day).SelectMany(s => s.Assignments).Should().BeEmpty();
        preview.Roster.Shifts.Single(s => s.WorkDate == w.Day.AddDays(1) && s.Assignments.Count > 0).StartTime.Hour.Should().Be(6);
    }
}
