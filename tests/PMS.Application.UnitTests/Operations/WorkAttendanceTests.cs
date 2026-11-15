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
using Xunit;

namespace PMS.Application.UnitTests.Operations;

public class WorkAttendanceTests
{
    private sealed class World : IDisposable
    {
        public OperationsTestContext T;
        public DatabaseContext Identity;
        public WorkSchedulingService Service;
        public DutyRosterModel Roster;
        public int Id => Roster.Shifts.First().Assignments.Single().Id;
        public DateTime Start => Roster.Shifts.First().StartsAt;
        public static async Task<World> Create(bool publish = true)
        {
            var t = await new OperationsTestContext().SeedAsync();
            t.Clock.Now = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
            t.ActAs(t.ManagerUserId, "Manager");
            var identity = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, t.Claims);
            identity.Users.Add(new ApplicationUser { Id = t.StaffUserId, Role = "Staff", IsActive = true });
            await identity.SaveChangesAsync();
            var service = new WorkSchedulingService(t.Db, identity, new LotScopeService(t.Db, t.Claims), t.Clock);
            var template = await service.SaveTemplateAsync(t.Lot.Id, null, new WorkShiftTemplateInput { Name = "Morning", StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(16, 0), MinimumEntryStaff = 1 });
            var day = new DateOnly(2026, 10, 20);
            var r = await service.CreateRosterAsync(t.Lot.Id, new CreateDutyRosterInput { Name = "Attendance", FromDate = day, ToDate = day.AddDays(1) });
            r = await service.AssignFromCalendarAsync(t.Lot.Id, r.Id, new CalendarAssignmentInput { Revision = r.Revision, WorkDate = day, WorkShiftTemplateId = template.Id, EmployeeId = t.Db.Employees.Single(e => e.UserId == t.StaffUserId).Id, Position = "Entry" });
            if (publish) r = await service.PublishAsync(t.Lot.Id, r.Id, new RosterMutationInput { Revision = r.Revision });
            t.ActAs(t.StaffUserId, "Staff");
            return new World { T = t, Identity = identity, Service = service, Roster = r };
        }
        public void At(double minute) => T.Clock.Now = new DateTimeOffset(Start.AddMinutes(minute));
        public AttendanceMutationInput Command(int revision = 0, string reason = null) => new() { Revision = revision, RosterRevision = Roster.Revision, Reason = reason };
        public void Manager() => T.ActAs(T.ManagerUserId, "Manager");
        public void Dispose() { T.Db.Dispose(); Identity.Dispose(); }
    }

    [Theory]
    [InlineData(-31, "Upcoming", false)]
    [InlineData(-30, "StartWindow", true)]
    [InlineData(15, "StartWindow", true)]
    [InlineData(30, "StartWindow", true)]
    [InlineData(30.01, "Absent", false)]
    public async Task Start_Window_And_Absence_Boundaries_Are_Server_Controlled(double minute, string expected, bool canStart)
    {
        using var w = await World.Create(); w.At(minute);
        var state = (await w.Service.AttendanceDetailAsync(w.Id)).Attendance;
        state.Status.Should().Be(expected); state.CanStart.Should().Be(canStart);
        if (!canStart) await FluentActions.Invoking(() => w.Service.StartAssignmentAsync(w.Id, w.Command())).Should().ThrowAsync<BadRequestException>();
    }

    [Theory]
    [InlineData(-30, false)]
    [InlineData(15, false)]
    [InlineData(15.01, true)]
    [InlineData(30, true)]
    public async Task Start_Records_Actual_Time_And_Late_Is_Only_A_Flag(double minute, bool late)
    {
        using var w = await World.Create(); w.At(minute);
        var result = await w.Service.StartAssignmentAsync(w.Id, w.Command());
        result.Attendance.Status.Should().Be("Working");
        result.Attendance.IsLate.Should().Be(late);
        result.Attendance.CheckInAt.Should().Be(w.T.Now);
        result.PlannedStart.Should().Be(w.Start);
        result.History.Should().ContainSingle(e => e.Action == "Started");
        var grid = await w.Service.MyScheduleAsync(new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 20));
        grid.Single().Shifts.Single().Assignments.Single().Attendance.Status.Should().Be("Working");
    }

    [Fact]
    public async Task Staff_End_Records_Actual_Out_And_Cannot_Be_Repeated()
    {
        using var w = await World.Create(); w.At(0);
        await w.Service.StartAssignmentAsync(w.Id, w.Command()); w.At(120);
        var result = await w.Service.EndAssignmentAsync(w.Id, w.Command(1));
        result.Attendance.Status.Should().Be("Completed"); result.Attendance.CheckOutAt.Should().Be(w.T.Now);
        result.Attendance.AutoEnded.Should().BeFalse(); result.History.Should().HaveCount(2);
        await FluentActions.Invoking(() => w.Service.EndAssignmentAsync(w.Id, w.Command(1))).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Rescheduling_Into_The_Past_Does_Not_Create_Retroactive_Absence()
    {
        using var w = await World.Create(); w.At(-31); w.Manager();
        var template = await w.Service.SaveTemplateAsync(w.T.Lot.Id, null, new WorkShiftTemplateInput {
            Name = "Earlier", StartTime = new TimeOnly(6, 0), EndTime = new TimeOnly(14, 0) });
        w.Roster = await w.Service.SaveShiftAsync(w.T.Lot.Id, w.Roster.Id, w.Roster.Shifts.Single().Id,
            new RosterShiftInput { Revision = w.Roster.Revision, Reason = "Correct planned time", WorkDate = new DateOnly(2026, 10, 20), WorkShiftTemplateId = template.Id });
        (await w.Service.AttendanceDetailAsync(w.Id)).Attendance.Status.Should().Be("Untracked");
        (await w.Service.UpdateDueAttendanceAsync()).Should().Be(0);
        w.T.Db.DutyAttendanceEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Timer_Marks_Absent_Only_After_Thirty_Minutes_And_Is_Idempotent()
    {
        using var w = await World.Create(); w.At(30);
        (await w.Service.UpdateDueAttendanceAsync()).Should().Be(0);
        w.At(30.01); (await w.Service.UpdateDueAttendanceAsync()).Should().Be(1);
        (await w.Service.UpdateDueAttendanceAsync()).Should().Be(0);
        w.T.Db.DutyAttendances.Single().IsAbsent.Should().BeTrue();
        w.T.Db.DutyAttendanceEvents.Should().ContainSingle(e => e.Action == "Absent");
        w.T.Db.UserNotifications.Count(n => n.Type == "AttendanceChanged").Should().Be(2);
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Cancelled")]
    [InlineData("Inactive")]
    [InlineData("Historical")]
    public async Task Draft_Cancelled_Inactive_And_PreTracking_Assignments_Are_Not_Absent(string scenario)
    {
        using var w = await World.Create(scenario != "Draft");
        var assignment = w.T.Db.DutyAssignments.Single();
        if (scenario == "Cancelled") assignment.IsCancelled = true;
        if (scenario == "Inactive") w.T.Db.Employees.Single(e => e.UserId == w.T.StaffUserId).Status = "Inactive";
        w.At(600);
        if (scenario == "Historical") assignment.AttendanceTrackingFrom = w.T.Now;
        await w.T.Db.SaveChangesAsync();
        (await w.Service.UpdateDueAttendanceAsync()).Should().Be(0);
        w.T.Db.DutyAttendances.Should().BeEmpty();
        if (scenario == "Historical") (await w.Service.AttendanceDetailAsync(w.Id)).Attendance.Status.Should().Be("Untracked");
    }

    [Fact]
    public async Task A_Roster_Published_After_The_Deadline_Is_Not_Retroactively_Absent()
    {
        using var w = await World.Create(false); w.At(31); w.Manager();
        w.Roster = await w.Service.PublishAsync(w.T.Lot.Id, w.Roster.Id, new RosterMutationInput { Revision = w.Roster.Revision });
        (await w.Service.AttendanceDetailAsync(w.Id)).Attendance.Status.Should().Be("Untracked");
        (await w.Service.UpdateDueAttendanceAsync()).Should().Be(0);
        w.T.Db.DutyAttendances.Should().BeEmpty();
    }

    [Fact]
    public async Task Grace_Boundary_AutoEnds_At_Planned_End_With_Review_Flag()
    {
        using var w = await World.Create(); w.At(0); await w.Service.StartAssignmentAsync(w.Id, w.Command());
        w.At(495); (await w.Service.UpdateDueAttendanceAsync()).Should().Be(0);
        (await w.Service.AttendanceDetailAsync(w.Id)).Attendance.Status.Should().Be("Working");
        w.At(495.01); (await w.Service.UpdateDueAttendanceAsync()).Should().Be(1);
        var detail = await w.Service.AttendanceDetailAsync(w.Id);
        detail.Attendance.Status.Should().Be("Completed"); detail.Attendance.AutoEnded.Should().BeTrue();
        detail.Attendance.CheckOutAt.Should().Be(detail.PlannedEnd);
        detail.History.First().Action.Should().Be("AutoEnded");
        (await w.Service.UpdateDueAttendanceAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Reopen_Needs_Manager_Reason_And_Clears_Absence()
    {
        using var w = await World.Create(); w.At(31); await w.Service.UpdateDueAttendanceAsync();
        await FluentActions.Invoking(() => w.Service.ReopenAssignmentAsync(w.Id, w.Command(1, "Late arrival"))).Should().ThrowAsync<ForbiddenException>();
        w.Manager();
        await FluentActions.Invoking(() => w.Service.ReopenAssignmentAsync(w.Id, w.Command(1))).Should().ThrowAsync<BadRequestException>();
        var result = await w.Service.ReopenAssignmentAsync(w.Id, w.Command(1, "Verified arrival"));
        result.Attendance.Status.Should().Be("Working"); result.Attendance.CheckInAt.Should().Be(w.T.Now);
        w.T.Db.DutyAttendances.Single().IsAbsent.Should().BeFalse(); result.History.First().Reason.Should().Be("Verified arrival");
    }

    [Fact]
    public async Task Manager_Correction_Preserves_AutoEnd_History_And_Requires_Valid_Times()
    {
        using var w = await World.Create(); w.At(0); await w.Service.StartAssignmentAsync(w.Id, w.Command());
        w.At(500); await w.Service.UpdateDueAttendanceAsync(); w.Manager();
        var input = new AttendanceAdjustmentInput { Revision = 2, RosterRevision = w.Roster.Revision, CheckInAt = w.Start.AddMinutes(5), CheckOutAt = w.Start.AddMinutes(490) };
        await FluentActions.Invoking(() => w.Service.AdjustAttendanceAsync(w.Id, input)).Should().ThrowAsync<BadRequestException>();
        input.Reason = "Verified actual handover";
        var result = await w.Service.AdjustAttendanceAsync(w.Id, input);
        result.Attendance.AutoEnded.Should().BeFalse(); result.Attendance.CheckOutAt.Should().Be(w.Start.AddMinutes(490));
        result.History.Should().HaveCount(3); result.History.Should().Contain(e => e.Action == "AutoEnded");
        input.Revision = 3; input.CheckOutAt = w.Start.AddMinutes(501);
        await FluentActions.Invoking(() => w.Service.AdjustAttendanceAsync(w.Id, input)).Should().ThrowAsync<BadRequestException>();
    }

    [Theory]
    [InlineData("other-staff", "Staff")]
    [InlineData("other-manager", "Manager")]
    [InlineData("admin", "Admin")]
    public async Task Attendance_Is_Scoped_To_Owner_Or_Lot_Manager(string userId, string role)
    {
        using var w = await World.Create(); w.T.ActAs(userId, role);
        await FluentActions.Invoking(() => w.Service.AttendanceDetailAsync(w.Id)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => w.Service.StartAssignmentAsync(w.Id, w.Command())).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Attendance_Cannot_Be_Erased_By_Removing_Or_Moving_The_Assignment()
    {
        using var w = await World.Create(); w.At(0); await w.Service.StartAssignmentAsync(w.Id, w.Command()); w.Manager();
        await FluentActions.Invoking(() => w.Service.RemoveAssignmentAsync(w.T.Lot.Id, w.Roster.Id, w.Roster.Shifts.Single().Id, w.Id,
            new RosterMutationInput { Revision = w.Roster.Revision, Reason = "Remove" })).Should().ThrowAsync<BadRequestException>();
        w.T.Db.DutyAssignments.Single().IsCancelled.Should().BeFalse();
    }
}
