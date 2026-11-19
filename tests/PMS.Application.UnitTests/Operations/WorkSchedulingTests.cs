using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.WorkScheduling;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

public class WorkSchedulingTests
{
    private static readonly DateOnly Day = new(2026, 11, 16);

    private sealed class Fixture : IDisposable
    {
        public OperationsTestContext T { get; private init; }
        public DatabaseContext Identity { get; private init; }
        public WorkSchedulingService Service { get; private init; }
        public int EmployeeId => T.Db.Employees.Single(e => e.UserId == T.StaffUserId).Id;
        public static async Task<Fixture> CreateAsync()
        {
            var t = await new OperationsTestContext().SeedAsync();
            t.ActAs(t.ManagerUserId, "Manager");
            var identity = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, t.Claims);
            identity.Users.Add(new ApplicationUser { Id = t.StaffUserId, UserName = "staff", Role = "Staff", IsActive = true });
            await identity.SaveChangesAsync();
            return new Fixture { T = t, Identity = identity, Service = new WorkSchedulingService(t.Db, identity, new LotScopeService(t.Db, t.Claims), t.Clock) };
        }
        public Task<WorkShiftTemplate> Template(string name = "Night", int start = 22, int end = 6) =>
            Service.SaveTemplateAsync(T.Lot.Id, null, new WorkShiftTemplateInput { Name = name, StartTime = new TimeOnly(start, 0), EndTime = new TimeOnly(end, 0), MinimumStaff = 2 });
        public Task<DutyRosterModel> Roster() => Service.CreateRosterAsync(T.Lot.Id, new CreateDutyRosterInput { Name = "Week", FromDate = Day, ToDate = Day.AddDays(6) });
        public Task<DutyRosterModel> Shift(DutyRosterModel r, WorkShiftTemplate t, DateOnly? day = null) => Service.SaveShiftAsync(T.Lot.Id, r.Id, null, new RosterShiftInput { WorkShiftTemplateId = t.Id, WorkDate = day ?? Day, Revision = r.Revision });
        public Task<DutyRosterModel> Assign(DutyRosterModel r, int? employee = null, int? shift = null, string reason = null) =>
            Service.SaveAssignmentAsync(T.Lot.Id, r.Id, shift ?? r.Shifts.Last().Id, null, new DutyAssignmentInput { EmployeeId = employee ?? EmployeeId, Revision = r.Revision, Reason = reason });
        public void Dispose() { T.Db.Dispose(); Identity.Dispose(); }
    }

    [Fact]
    public async Task Overnight_Shift_Uses_Lot_Time_And_Is_Hidden_Until_Published()
    {
        using var f = await Fixture.CreateAsync();
        var r = await f.Shift(await f.Roster(), await f.Template());
        var shift = r.Shifts.Single();
        shift.StartsAt.Should().Be(new DateTime(2026, 11, 16, 15, 0, 0, DateTimeKind.Utc));
        shift.EndsAt.Should().Be(shift.StartsAt.AddHours(8));
        r = await f.Assign(r);
        f.T.ActAs(f.T.StaffUserId, "Staff");
        (await f.Service.MyScheduleAsync(Day, Day.AddDays(1))).Should().BeEmpty();
        f.T.ActAs(f.T.ManagerUserId, "Manager");
        r = await f.Service.PublishAsync(f.T.Lot.Id, r.Id, new RosterMutationInput { Revision = r.Revision });
        r.Status.Should().Be("Published");
        r.Shortages.Should().ContainSingle(x => x.Required == 2 && x.Assigned == 1);
        f.T.ActAs(f.T.StaffUserId, "Staff");
        var mine = await f.Service.MyScheduleAsync(Day.AddDays(1), Day.AddDays(1));
        mine.Should().ContainSingle();
        mine.Single().Shifts.Single().Assignments.Should().ContainSingle(a => a.EmployeeId == f.EmployeeId);
        mine.Single().Shortages.Should().BeEmpty();
        f.T.Db.UserNotifications.Should().ContainSingle(n => n.UserId == f.T.StaffUserId && n.Type == "RosterPublished");
    }

    [Fact]
    public async Task Template_Edits_And_Deactivation_Do_Not_Change_Published_Shifts()
    {
        using var f = await Fixture.CreateAsync();
        var t = await f.Template();
        var r = await f.Assign(await f.Shift(await f.Roster(), t));
        r = await f.Service.PublishAsync(f.T.Lot.Id, r.Id, new RosterMutationInput { Revision = r.Revision });
        await f.Service.SaveTemplateAsync(f.T.Lot.Id, t.Id, new WorkShiftTemplateInput { Name = "Changed", StartTime = new TimeOnly(20, 0), EndTime = new TimeOnly(4, 0), MinimumStaff = 9, Revision = t.Revision, IsActive = false });
        var saved = (await f.Service.RostersAsync(f.T.Lot.Id)).Single().Shifts.Single();
        saved.Name.Should().Be("Night");
        saved.StartTime.Should().Be(new TimeOnly(22, 0));
        saved.MinimumStaff.Should().Be(2);
        saved.Assignments.Should().ContainSingle();
    }

    [Theory]
    [InlineData("Staff")]
    [InlineData("Admin")]
    [InlineData("User")]
    public async Task Only_Manager_Can_Edit_Templates_And_Rosters(string role)
    {
        using var f = await Fixture.CreateAsync();
        f.T.ActAs(f.T.ManagerUserId, role);
        await FluentActions.Invoking(() => f.Template()).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => f.Roster()).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => f.Service.StaffAsync(f.T.Lot.Id)).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Other_Manager_Cannot_Read_Or_Write_The_Lot()
    {
        using var f = await Fixture.CreateAsync();
        var r = await f.Roster();
        f.T.ActAs("other-manager", "Manager");
        await FluentActions.Invoking(() => f.Template()).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => f.Service.RostersAsync(f.T.Lot.Id)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => f.Service.HistoryAsync(f.T.Lot.Id, r.Id)).Should().ThrowAsync<ForbiddenException>();
    }

    [Theory]
    [InlineData("Inactive", true, false)]
    [InlineData("Terminated", true, false)]
    [InlineData("Active", false, false)]
    [InlineData("Active", true, true)]
    public async Task Rejects_Inactive_Accounts_And_Staff_From_Other_Lots(string status, bool active, bool otherLot)
    {
        using var f = await Fixture.CreateAsync();
        var r = await f.Shift(await f.Roster(), await f.Template());
        var e = f.T.Db.Employees.Single(e => e.Id == f.EmployeeId);
        e.Status = status;
        if (otherLot) e.ParkingLotId = 999;
        f.Identity.Users.Single().IsActive = active;
        await f.T.Db.SaveChangesAsync();
        await f.Identity.SaveChangesAsync();
        await FluentActions.Invoking(() => f.Assign(r)).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Overnight_Overlap_Across_Rosters_Is_Rejected_But_Touching_Shifts_Are_Allowed()
    {
        using var f = await Fixture.CreateAsync();
        await f.Assign(await f.Shift(await f.Roster(), await f.Template()));
        var morning = await f.Template("Early", 5, 13);
        var r = await f.Shift(await f.Roster(), morning, Day.AddDays(1));
        await FluentActions.Invoking(() => f.Assign(r)).Should().ThrowAsync<BadRequestException>().WithMessage("*overlapping*");
        // A different roster at the exact end of the overnight shift is valid.
        var boundary = await f.Template("Morning", 6, 14);
        var next = await f.Shift(await f.Roster(), boundary, Day.AddDays(1));
        (await f.Assign(next)).Shifts.Single().Assignments.Should().ContainSingle();
    }

    [Fact]
    public async Task Cannot_Assign_One_Staff_Twice_To_The_Same_Shift()
    {
        using var f = await Fixture.CreateAsync();
        var r = await f.Assign(await f.Shift(await f.Roster(), await f.Template()));
        await FluentActions.Invoking(() => f.Assign(r)).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Publication_Revalidates_Staff_And_Cannot_Expose_Other_Staff_Assignments()
    {
        using var f = await Fixture.CreateAsync();
        var r = await f.Assign(await f.Shift(await f.Roster(), await f.Template()));
        f.Identity.Users.Single().IsActive = false;
        await f.Identity.SaveChangesAsync();
        await FluentActions.Invoking(() => f.Service.PublishAsync(f.T.Lot.Id, r.Id, new RosterMutationInput { Revision = r.Revision }))
            .Should().ThrowAsync<BadRequestException>();
        f.T.Db.DutyRosters.Single().Status.Should().Be("Draft");
        f.T.Db.UserNotifications.Should().BeEmpty();
        f.Identity.Users.Single().IsActive = true;
        await f.Identity.SaveChangesAsync();
        await f.Service.PublishAsync(f.T.Lot.Id, r.Id, new RosterMutationInput { Revision = r.Revision });
        f.T.ActAs("another-staff", "Staff");
        (await f.Service.MyScheduleAsync(Day, Day)).Should().BeEmpty();
    }

    [Fact]
    public async Task Published_Replacement_Records_Before_And_After_And_Only_Notifies_Affected_Staff()
    {
        using var f = await Fixture.CreateAsync();
        var r = await f.Assign(await f.Shift(await f.Roster(), await f.Template()));
        var second = new Employee { UserId = "staff-2", EmployeeCode = "EMP-2", FullName = "Replacement", ParkingLotId = f.T.Lot.Id, Status = "Active", Position = "Attendant" };
        var unaffected = new Employee { UserId = "staff-3", EmployeeCode = "EMP-3", FullName = "Unaffected", ParkingLotId = f.T.Lot.Id, Status = "Active", Position = "Attendant" };
        f.T.Db.Employees.AddRange(second, unaffected);
        f.Identity.Users.AddRange(new ApplicationUser { Id = second.UserId, Role = "Staff", IsActive = true }, new ApplicationUser { Id = unaffected.UserId, Role = "Staff", IsActive = true });
        await f.T.Db.SaveChangesAsync(); await f.Identity.SaveChangesAsync();
        r = await f.Assign(r, unaffected.Id);
        r = await f.Service.PublishAsync(f.T.Lot.Id, r.Id, new RosterMutationInput { Revision = r.Revision });
        var a = r.Shifts.Single().Assignments.Single(a => a.EmployeeId == f.EmployeeId);
        var input = new DutyAssignmentInput { Revision = r.Revision, EmployeeId = second.Id };
        await FluentActions.Invoking(() => f.Service.SaveAssignmentAsync(f.T.Lot.Id, r.Id, r.Shifts.Single().Id, a.Id, input))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*reason*");
        input.Reason = "Replace unavailable Staff";
        r = await f.Service.SaveAssignmentAsync(f.T.Lot.Id, r.Id, r.Shifts.Single().Id, a.Id, input);
        r.Status.Should().Be("Published");
        var notices = f.T.Db.UserNotifications.Where(n => n.Type == "RosterChanged").ToList();
        notices.Select(n => n.UserId).Should().BeEquivalentTo(f.T.StaffUserId, second.UserId);
        notices.Should().NotContain(n => n.UserId == unaffected.UserId);
        var history = (await f.Service.HistoryAsync(f.T.Lot.Id, r.Id)).First();
        history.BeforeJson.Should().Contain($"\"EmployeeId\":{f.EmployeeId}");
        history.AfterJson.Should().Contain("Replacement");
        history.Reason.Should().Be(input.Reason);
        history.ChangedBy.Should().Be(f.T.ManagerUserId);
    }

    [Fact]
    public async Task Cancelling_Published_Shift_Keeps_Rows_And_Notifies_Removed_Staff()
    {
        using var f = await Fixture.CreateAsync();
        var r = await f.Assign(await f.Shift(await f.Roster(), await f.Template()));
        r = await f.Service.PublishAsync(f.T.Lot.Id, r.Id, new RosterMutationInput { Revision = r.Revision });
        var next = await f.Service.RemoveShiftAsync(f.T.Lot.Id, r.Id, r.Shifts.Single().Id, new RosterMutationInput { Revision = r.Revision, Reason = "Lane closed" });
        next.Shifts.Should().BeEmpty();
        f.T.Db.DutyRosterShifts.Should().ContainSingle(s => s.IsCancelled);
        f.T.Db.DutyAssignments.Should().ContainSingle(a => a.IsCancelled);
        f.T.Db.UserNotifications.Should().ContainSingle(n => n.Type == "RosterChanged" && n.UserId == f.T.StaffUserId);
        f.T.ActAs(f.T.StaffUserId, "Staff");
        (await f.Service.MyScheduleAsync(Day, Day)).Should().BeEmpty();
    }

    [Fact]
    public async Task Stale_Roster_And_Template_Revision_Are_Rejected()
    {
        using var f = await Fixture.CreateAsync();
        var t = await f.Template();
        var old = await f.Roster();
        await f.Shift(old, t);
        await FluentActions.Invoking(() => f.Service.PublishAsync(f.T.Lot.Id, old.Id, new RosterMutationInput { Revision = old.Revision })).Should().ThrowAsync<ConflictException>();
        await FluentActions.Invoking(() => f.Service.SaveTemplateAsync(f.T.Lot.Id, t.Id, new WorkShiftTemplateInput { Name = "Changed", StartTime = t.StartTime, EndTime = t.EndTime, Revision = 0 })).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Shift_Ending_Exactly_At_Midnight_Does_Not_Appear_On_The_Next_Day()
    {
        using var f = await Fixture.CreateAsync();
        var r = await f.Assign(await f.Shift(await f.Roster(), await f.Template("Evening", 16, 0)));
        await f.Service.PublishAsync(f.T.Lot.Id, r.Id, new RosterMutationInput { Revision = r.Revision });
        f.T.ActAs(f.T.StaffUserId, "Staff");
        (await f.Service.MyScheduleAsync(Day.AddDays(1), Day.AddDays(1))).Should().BeEmpty();
        (await f.Service.MyScheduleAsync(Day, Day)).Should().ContainSingle();
    }

    [Fact]
    public async Task Template_And_Roster_Ids_Cannot_Be_Reused_Across_Lots()
    {
        using var f = await Fixture.CreateAsync();
        var otherLot = new ParkingLot { Code = "LOT-OTHER", Name = "Other lot", Address = "Other", ManagerUserId = f.T.ManagerUserId, Status = "Active" };
        f.T.Db.ParkingLots.Add(otherLot);
        await f.T.Db.SaveChangesAsync();
        var template = await f.Service.SaveTemplateAsync(otherLot.Id, null, new WorkShiftTemplateInput { Name = "Other", StartTime = new TimeOnly(6, 0), EndTime = new TimeOnly(14, 0) });
        var r = await f.Roster();
        await FluentActions.Invoking(() => f.Shift(r, template)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Invoking(() => f.Service.HistoryAsync(otherLot.Id, r.Id)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Calendar_Assignment_Creates_One_Dated_Shift_And_One_History_Revision()
    {
        using var f = await Fixture.CreateAsync();
        var t = await f.Template();
        var r = await f.Roster();
        var result = await f.Service.AssignFromCalendarAsync(f.T.Lot.Id, r.Id, new CalendarAssignmentInput
        {
            Revision = r.Revision, WorkDate = Day, WorkShiftTemplateId = t.Id,
            EmployeeId = f.EmployeeId, Notes = "  Gate A handover  "
        });
        result.Revision.Should().Be(r.Revision + 1);
        var shift = result.Shifts.Single();
        shift.Assignments.Single().Notes.Should().Be("Gate A handover");
        shift.Assignments.Single().Status.Should().Be("Assigned");
        shift.Assigned.Should().Be(1);
        (await f.Service.HistoryAsync(f.T.Lot.Id, r.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Failed_Calendar_Assignment_Does_Not_Create_An_Orphan_Shift()
    {
        using var f = await Fixture.CreateAsync();
        var t = await f.Template();
        var r = await f.Roster();
        await FluentActions.Invoking(() => f.Service.AssignFromCalendarAsync(f.T.Lot.Id, r.Id,
            new CalendarAssignmentInput { Revision = r.Revision, WorkDate = Day, WorkShiftTemplateId = t.Id, EmployeeId = 999 }))
            .Should().ThrowAsync<BadRequestException>();
        f.T.Db.DutyRosterShifts.Should().BeEmpty();
        f.T.Db.DutyAssignments.Should().BeEmpty();
    }

    [Fact]
    public async Task Eligibility_Uses_Overnight_Overlap_And_Cannot_Exclude_Another_Rosters_Assignment()
    {
        using var f = await Fixture.CreateAsync();
        var night = await f.Assign(await f.Shift(await f.Roster(), await f.Template()));
        var morning = await f.Shift(await f.Roster(), await f.Template("Early", 5, 13), Day.AddDays(1));
        (await f.Service.EligibleStaffAsync(f.T.Lot.Id, morning.Id, morning.Shifts.Single().Id, default, null, null)).Should().BeEmpty();
        var own = night.Shifts.Single().Assignments.Single();
        (await f.Service.EligibleStaffAsync(f.T.Lot.Id, night.Id, night.Shifts.Single().Id, default, null, own.Id)).Should().ContainSingle(e => e.EmployeeId == f.EmployeeId);
        await FluentActions.Invoking(() => f.Service.EligibleStaffAsync(f.T.Lot.Id, morning.Id, morning.Shifts.Single().Id, default, null, own.Id))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Moving_Assignment_Keeps_Identity_And_Published_Note_Changes_Notify_Staff()
    {
        using var f = await Fixture.CreateAsync();
        var r = await f.Assign(await f.Shift(await f.Roster(), await f.Template("Morning", 6, 14)));
        r = await f.Shift(r, await f.Template("Afternoon", 14, 22));
        var original = r.Shifts.First();
        var target = r.Shifts.Last();
        var a = original.Assignments.Single();
        r = await f.Service.SaveAssignmentAsync(f.T.Lot.Id, r.Id, original.Id, a.Id, new DutyAssignmentInput {
            Revision = r.Revision, EmployeeId = f.EmployeeId, TargetShiftId = target.Id, Notes = "Counter task" });
        r.Shifts.First().Assignments.Should().BeEmpty();
        r.Shifts.Last().Assignments.Single().Id.Should().Be(a.Id);
        r.Shifts.Last().Assigned.Should().Be(1);
        r = await f.Service.PublishAsync(f.T.Lot.Id, r.Id, new RosterMutationInput { Revision = r.Revision });
        r = await f.Service.SaveAssignmentAsync(f.T.Lot.Id, r.Id, target.Id, a.Id, new DutyAssignmentInput {
            Revision = r.Revision, EmployeeId = f.EmployeeId, Notes = "Updated handover", Reason = "Handover instructions changed" });
        f.T.Db.UserNotifications.Should().ContainSingle(n => n.Type == "RosterChanged" && n.UserId == f.T.StaffUserId);
        (await f.Service.HistoryAsync(f.T.Lot.Id, r.Id)).First().AfterJson.Should().Contain("Updated handover");
    }

    [Fact]
    public async Task Personal_Schedule_Retains_Total_Staffing_Without_Disclosing_Other_Staff_Assignments()
    {
        using var f = await Fixture.CreateAsync();
        var r = await f.Assign(await f.Shift(await f.Roster(), await f.Template()));
        var other = new Employee { UserId = "other-staff", EmployeeCode = "OTHER", FullName = "Other Staff", Position = "Attendant", Status = "Active", ParkingLotId = f.T.Lot.Id };
        f.T.Db.Employees.Add(other);
        f.Identity.Users.Add(new ApplicationUser { Id = other.UserId, Role = "Staff", IsActive = true });
        await f.T.Db.SaveChangesAsync(); await f.Identity.SaveChangesAsync();
        r = await f.Assign(r, other.Id);
        await f.Service.PublishAsync(f.T.Lot.Id, r.Id, new RosterMutationInput { Revision = r.Revision });
        f.T.ActAs(f.T.StaffUserId, "Staff");
        var shift = (await f.Service.MyScheduleAsync(Day, Day)).Single().Shifts.Single();
        shift.Assigned.Should().Be(2);
        shift.Assignments.Should().ContainSingle(a => a.EmployeeId == f.EmployeeId);
        shift.Assignments.Should().NotContain(a => a.EmployeeId == other.Id);
    }

    [Fact]
    public async Task Calendar_Does_Not_Refresh_An_Existing_Snapshot_Or_Accept_Cross_Roster_Move()
    {
        using var f = await Fixture.CreateAsync();
        var t = await f.Template();
        var r = await f.Shift(await f.Roster(), t);
        await f.Service.SaveTemplateAsync(f.T.Lot.Id, t.Id, new WorkShiftTemplateInput { Name = "New name", StartTime = new TimeOnly(20, 0), EndTime = new TimeOnly(4, 0), Revision = t.Revision });
        r = await f.Service.AssignFromCalendarAsync(f.T.Lot.Id, r.Id, new CalendarAssignmentInput {
            Revision = r.Revision, WorkDate = Day, WorkShiftTemplateId = t.Id, EmployeeId = f.EmployeeId });
        r.Shifts.Should().ContainSingle(s => s.Name == "Night" && s.StartTime == new TimeOnly(22, 0));
        var other = await f.Shift(await f.Roster(), t);
        await FluentActions.Invoking(() => f.Service.SaveAssignmentAsync(f.T.Lot.Id, r.Id, r.Shifts.Single().Id, r.Shifts.Single().Assignments.Single().Id,
            new DutyAssignmentInput { Revision = r.Revision, EmployeeId = f.EmployeeId, TargetShiftId = other.Shifts.Single().Id }))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Unfilled_Shifts_Can_Be_Published_With_Warnings_And_Equal_Times_Are_Invalid()
    {
        using var f = await Fixture.CreateAsync();
        await FluentActions.Invoking(() => f.Template("Invalid", 6, 6)).Should().ThrowAsync<BadRequestException>();
        var r = await f.Shift(await f.Roster(), await f.Template());
        var result = await f.Service.PublishAsync(f.T.Lot.Id, r.Id, new RosterMutationInput { Revision = r.Revision });
        result.Status.Should().Be("Published");
        result.Shortages.Should().ContainSingle(x => x.Required == 2 && x.Assigned == 0);
    }
}
