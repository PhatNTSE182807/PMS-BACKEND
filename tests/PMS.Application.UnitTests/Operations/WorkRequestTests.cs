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
public class WorkRequestTests
{
    private sealed class World : IDisposable
    {
        public OperationsTestContext T; public DatabaseContext Identity; public WorkSchedulingService S; public DutyRosterModel R;
        public int[] Employees; public int First => R.Shifts[0].Assignments.First(a => a.EmployeeId == Employees[0]).Id;
        public DateOnly Day = new(2026, 11, 20);
        public static async Task<World> Create(int minimum = 1)
        {
            var t = await new OperationsTestContext().SeedAsync(); t.Clock.Now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
            var identity = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, t.Claims);
            foreach (var user in new[] { t.StaffUserId, "staff-2", "staff-3" }) identity.Users.Add(new ApplicationUser { Id = user, Role = "Staff", IsActive = true });
            await identity.SaveChangesAsync();
            foreach (var n in new[] { 2, 3 }) t.Db.Employees.Add(new Employee { UserId = $"staff-{n}", FullName = $"Staff {n}", EmployeeCode = $"WR{n}", ParkingLotId = t.Lot.Id, Status = "Active", Position = "Staff" });
            await t.Db.SaveChangesAsync(); t.ActAs(t.ManagerUserId, "Manager");
            var s = new WorkSchedulingService(t.Db, identity, new LotScopeService(t.Db, t.Claims), t.Clock);
            var staff = (await s.StaffAsync(t.Lot.Id)).OrderBy(e => e.UserId).Select(e => e.EmployeeId).ToArray();
            var template = await s.SaveTemplateAsync(t.Lot.Id, null, new WorkShiftTemplateInput { Name = "Day", StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(16, 0), MinimumStaff = minimum });
            var day = new DateOnly(2026, 11, 20);
            var r = await s.CreateRosterAsync(t.Lot.Id, new CreateDutyRosterInput { Name = "Requests", FromDate = day, ToDate = day.AddDays(2) });
            r = await s.AssignFromCalendarAsync(t.Lot.Id, r.Id, new CalendarAssignmentInput { Revision = r.Revision, EmployeeId = staff[0], WorkDate = day, WorkShiftTemplateId = template.Id });
            r = await s.AssignFromCalendarAsync(t.Lot.Id, r.Id, new CalendarAssignmentInput { Revision = r.Revision, EmployeeId = staff[1], WorkDate = day.AddDays(1), WorkShiftTemplateId = template.Id });
            r = await s.PublishAsync(t.Lot.Id, r.Id, new RosterMutationInput { Revision = r.Revision });
            return new World { T = t, Identity = identity, S = s, R = r, Employees = staff };
        }
        public void Staff(int n = 0) => T.ActAs(T.Db.Employees.Single(e => e.Id == Employees[n]).UserId, "Staff");
        public void Manager() => T.ActAs(T.ManagerUserId, "Manager");
        public async Task<WorkLeaveRequest> Leave(string kind = "Planned") { Staff(); return await S.CreateLeaveAsync(new CreateLeaveInput { AssignmentId = First, Kind = kind, Reason = "Personal leave" }); }
        public async Task Approve(WorkLeaveRequest leave) { Manager(); await S.DecideLeaveAsync(leave.Id, new WorkRequestCommand { Revision = leave.Revision, Action = "Approve", Reason = "Approved by lot Manager" }); }
        public void Dispose() { T.Db.Dispose(); Identity.Dispose(); }
    }

    [Fact]
    public async Task Pending_Leave_Preserves_Assignment_And_Staff_Can_Cancel()
    {
        using var w = await World.Create(); var request = await w.Leave();
        var mine = await w.S.MyScheduleAsync(w.Day, w.Day);
        mine.Single().Shifts.Single().Assignments.Single().RequestState.Should().Be("LeavePending");
        w.T.Db.DutyAssignments.Single(a => a.Id == w.First).IsCancelled.Should().BeFalse();
        await w.S.DecideLeaveAsync(request.Id, new WorkRequestCommand { Revision = request.Revision, Action = "Cancel" });
        w.T.Db.WorkLeaveDays.Should().BeEmpty(); w.T.Db.WorkOpenShifts.Should().BeEmpty();
        await FluentActions.Invoking(() => w.S.DecideLeaveAsync(request.Id, new WorkRequestCommand { Revision = 1, Action = "Cancel" })).Should().ThrowAsync<ConflictException>();
    }
    [Fact]
    public async Task Approval_Retains_Leave_Trail_And_Claim_Is_Not_Counting_As_Staffing()
    {
        using var w = await World.Create(); var request = await w.Leave(); await w.Approve(request);
        var original = w.T.Db.DutyAssignments.Single(a => a.Id == w.First); original.IsCancelled.Should().BeTrue();
        var slot = w.T.Db.WorkOpenShifts.Single(); slot.Status.Should().Be("Open"); slot.ExpiresAt.Should().Be(w.R.Shifts[0].StartsAt);
        w.Staff(1); await w.S.ActOnOpenShiftAsync(slot.Id, new WorkRequestCommand { Revision = slot.Revision, Action = "Claim" });
        w.Manager(); var model = (await w.S.RostersAsync(w.T.Lot.Id)).Single();
        model.Shifts[0].Assigned.Should().Be(0); model.Shifts[0].Trail.Should().ContainSingle(t => t.Kind == "ApprovedLeave" && t.IsPaid);
        var claim = w.T.Db.WorkOpenShiftClaims.Single();
        await w.S.ActOnOpenShiftAsync(slot.Id, new WorkRequestCommand { Revision = slot.Revision, Action = "Confirm", ClaimId = claim.Id, Reason = "Replacement approved" });
        var replacement = w.T.Db.DutyAssignments.Single(a => a.OriginalAssignmentId == original.Id);
        replacement.EmployeeId.Should().Be(w.Employees[1]); replacement.Id.Should().NotBe(original.Id);
        (await w.S.RostersAsync(w.T.Lot.Id)).Single().Shifts[0].Assigned.Should().Be(1);
        claim.Status.Should().Be("Confirmed");
    }
    [Fact]
    public async Task Surplus_Shift_Does_Not_Require_A_Replacement_And_Exhausted_Leave_Is_Unpaid()
    {
        using var w = await World.Create(0);
        w.T.ActAs("admin", "Admin");
        await w.S.SetLeaveEntitlementAsync(w.Employees[0], 2026, new LeaveEntitlementInput { Revision = 0, EntitledDays = 0, Reason = "No balance remaining" });
        var leave = await w.Leave(); await w.Approve(leave);
        w.T.Db.WorkLeaveRequests.Single().IsPaid.Should().BeFalse(); w.T.Db.WorkOpenShifts.Should().BeEmpty();
        w.Staff(); var balance = (await w.S.RequestsAsync(w.T.Lot.Id, 2026)).Balances.Single(); balance.PaidDaysUsed.Should().Be(0); balance.UnpaidDays.Should().Be(1);
    }
    [Fact]
    public async Task Approving_Emergency_Leave_Clears_The_Absence_With_History()
    {
        using var w = await World.Create(); var plannedStart = w.R.Shifts[0].StartsAt;
        w.T.Clock.Now = new DateTimeOffset(plannedStart.AddMinutes(31));
        await w.S.UpdateDueAttendanceAsync();
        var leave = await w.Leave(kind: "Emergency"); await w.Approve(leave);
        w.T.Db.DutyAttendances.Single(a => a.DutyAssignmentId == w.First).IsAbsent.Should().BeFalse();
        w.T.Db.DutyAttendanceEvents.Should().Contain(e => e.Action == "LeaveApproved");
    }
    [Fact]
    public async Task Swap_Requires_Colleague_Consent_And_Replaces_Both_Originals()
    {
        using var w = await World.Create(); w.Staff();
        var second = w.R.Shifts[1].Assignments.Single().Id;
        var swap = await w.S.CreateSwapAsync(new CreateSwapInput { AssignmentId = w.First, OtherAssignmentId = second, Reason = "Trade dates" });
        w.Manager(); await FluentActions.Invoking(() => w.S.DecideSwapAsync(swap.Id, new WorkRequestCommand { Revision = swap.Revision, Action = "Approve", Reason = "Too early" })).Should().ThrowAsync<BadRequestException>();
        w.Staff(1); await w.S.DecideSwapAsync(swap.Id, new WorkRequestCommand { Revision = swap.Revision, Action = "Accept" });
        w.Manager(); await w.S.DecideSwapAsync(swap.Id, new WorkRequestCommand { Revision = swap.Revision, Action = "Approve", Reason = "Both parties approved" });
        w.T.Db.DutyAssignments.Where(a => a.Id == w.First || a.Id == second).Should().OnlyContain(a => a.IsCancelled);
        var replacements = w.T.Db.DutyAssignments.Where(a => a.Source == "Swap").ToList(); replacements.Should().HaveCount(2);
        replacements.Single(a => a.OriginalAssignmentId == w.First).EmployeeId.Should().Be(w.Employees[1]);
        replacements.Single(a => a.OriginalAssignmentId == second).EmployeeId.Should().Be(w.Employees[0]);
    }
    [Fact]
    public async Task Vacancy_Expires_At_Shift_Start_And_Reruns_Are_Idempotent()
    {
        using var w = await World.Create(); var leave = await w.Leave(); await w.Approve(leave); var slot = w.T.Db.WorkOpenShifts.Single();
        w.Staff(1); await w.S.ActOnOpenShiftAsync(slot.Id, new WorkRequestCommand { Revision = slot.Revision, Action = "Claim" });
        w.T.Clock.Now = new DateTimeOffset(w.R.Shifts[0].StartsAt.AddMinutes(-1));
        (await w.S.ProcessWorkRequestDeadlinesAsync()).Should().Be(0); slot.Status.Should().Be("Open");
        w.T.Clock.Now = new DateTimeOffset(w.R.Shifts[0].StartsAt);
        (await w.S.ProcessWorkRequestDeadlinesAsync()).Should().Be(1); slot.Status.Should().Be("Expired");
        w.T.Db.WorkOpenShiftClaims.Single().Status.Should().Be("Expired");
        (await w.S.ProcessWorkRequestDeadlinesAsync()).Should().Be(0);
        w.Manager(); await FluentActions.Invoking(() => w.S.ActOnOpenShiftAsync(slot.Id, new WorkRequestCommand { Revision = slot.Revision, Action = "ManagerCover", Reason = "Too late" }))
            .Should().ThrowAsync<ConflictException>();
    }
    [Theory]
    [InlineData("Admin", "admin")]
    [InlineData("Manager", "other-manager")]
    [InlineData("Staff", "not-at-this-lot")]
    public async Task Requests_Are_Scoped_By_Role_And_Lot(string role, string user)
    {
        using var w = await World.Create(); w.T.ActAs(user, role);
        await FluentActions.Invoking(() => w.S.RequestsAsync(w.T.Lot.Id, 2026)).Should().ThrowAsync<ForbiddenException>();
    }

    [Theory]
    [InlineData(24, "Planned")]
    [InlineData(23.99, "Emergency")]
    public async Task Leave_Type_Changes_Exactly_At_24_Hours(double hours, string expected)
    {
        using var w = await World.Create(); w.T.Clock.Now = new DateTimeOffset(w.R.Shifts[0].StartsAt.AddHours(-hours));
        (await w.Leave(kind: expected)).Kind.Should().Be(expected);
    }

    [Fact]
    public async Task Multiple_Assignments_On_One_Local_Date_Use_One_Paid_Day_And_Real_Leave_Source()
    {
        using var w = await World.Create(0); w.Manager();
        var template = await w.S.SaveTemplateAsync(w.T.Lot.Id, null, new WorkShiftTemplateInput { Name = "Evening", StartTime = new TimeOnly(17, 0), EndTime = new TimeOnly(20, 0) });
        w.R = await w.S.AssignFromCalendarAsync(w.T.Lot.Id, w.R.Id, new CalendarAssignmentInput { Revision = w.R.Revision, Reason = "Second assignment", WorkDate = w.Day, WorkShiftTemplateId = template.Id, EmployeeId = w.Employees[0] });
        var second = w.R.Shifts.Last(s => s.WorkDate == w.Day).Assignments.Single().Id;
        var leave = await w.Leave(); await w.Approve(leave);
        w.Staff(); var other = await w.S.CreateLeaveAsync(new CreateLeaveInput { AssignmentId = second, Kind = "Planned", Reason = "Same leave day" }); await w.Approve(other);
        w.T.Db.WorkLeaveDays.Should().ContainSingle(d => d.IsPaid);
        var source = new PMS.Application.Services.ApprovedWorkLeaveSource(w.T.Db);
        (await source.ReadAsync(w.T.Lot.Id, w.R.Shifts[0].StartsAt, w.R.Shifts[0].EndsAt)).Leaves.Should().NotBeEmpty();
        w.Staff(); var mine = (await w.S.MyScheduleAsync(w.Day, w.Day)).Single();
        mine.Shifts.SelectMany(s => s.Assignments).Should().BeEmpty(); mine.Shifts.SelectMany(s => s.Trail).Should().HaveCount(2);
    }

    [Fact]
    public async Task Approval_Rechecks_Conflict_After_Colleague_Consent_Without_Partial_Swap()
    {
        using var w = await World.Create(); w.Staff(); var second = w.R.Shifts[1].Assignments.Single().Id;
        var swap = await w.S.CreateSwapAsync(new CreateSwapInput { AssignmentId = w.First, OtherAssignmentId = second, Reason = "Trade" });
        w.Staff(1); await w.S.DecideSwapAsync(swap.Id, new WorkRequestCommand { Revision = swap.Revision, Action = "Accept" });
        w.Manager();
        var otherRoster = await w.S.CreateRosterAsync(w.T.Lot.Id, new CreateDutyRosterInput { Name = "Late conflict", FromDate = w.Day, ToDate = w.Day });
        var templateId = w.R.Shifts[0].WorkShiftTemplateId;
        await w.S.AssignFromCalendarAsync(w.T.Lot.Id, otherRoster.Id, new CalendarAssignmentInput { Revision = otherRoster.Revision, EmployeeId = w.Employees[1], WorkDate = w.Day, WorkShiftTemplateId = templateId });
        await FluentActions.Invoking(() => w.S.DecideSwapAsync(swap.Id, new WorkRequestCommand { Revision = swap.Revision, Action = "Approve", Reason = "Try approval" })).Should().ThrowAsync<BadRequestException>();
        w.T.Db.DutyAssignments.Where(a => a.Id == w.First || a.Id == second).Should().OnlyContain(a => !a.IsCancelled);
        w.T.Db.DutyAssignments.Where(a => a.Source == "Swap").Should().BeEmpty();
    }

    [Fact]
    public async Task Confirmed_InProgress_Replacement_Can_Start_Without_Inheriting_The_Original_Absence()
    {
        using var w = await World.Create(); w.T.Clock.Now = new DateTimeOffset(w.R.Shifts[0].StartsAt.AddHours(1));
        await w.S.UpdateDueAttendanceAsync(); var leave = await w.Leave(kind: "Emergency"); await w.Approve(leave);
        var slot = w.T.Db.WorkOpenShifts.Single(); slot.ExpiresAt.Should().Be(w.R.Shifts[0].EndsAt);
        w.Manager(); await w.S.ActOnOpenShiftAsync(slot.Id, new WorkRequestCommand { Revision = slot.Revision, Action = "Assign", EmployeeId = w.Employees[1], Reason = "Urgent replacement" });
        var replacement = w.T.Db.DutyAssignments.Single(a => a.OriginalAssignmentId == w.First);
        w.Staff(1); var detail = await w.S.AttendanceDetailAsync(replacement.Id);
        detail.Attendance.Status.Should().Be("StartWindow"); detail.Attendance.CanStart.Should().BeTrue();
        var started = await w.S.StartAssignmentAsync(replacement.Id, new AttendanceMutationInput { Revision = 0, RosterRevision = detail.RosterRevision });
        started.Attendance.Status.Should().Be("Working"); started.Attendance.IsLate.Should().BeFalse();
    }

    [Fact]
    public async Task Open_Offer_Cannot_Be_Bypassed_By_The_Manual_Calendar_Assignment_Route()
    {
        using var w = await World.Create(); var leave = await w.Leave(); await w.Approve(leave); w.Manager();
        var roster = (await w.S.RostersAsync(w.T.Lot.Id)).Single();
        await FluentActions.Invoking(() => w.S.SaveAssignmentAsync(w.T.Lot.Id, roster.Id, roster.Shifts[0].Id, null,
            new DutyAssignmentInput { Revision = roster.Revision, EmployeeId = w.Employees[1], Reason = "Try bypass" })).Should().ThrowAsync<BadRequestException>();
        w.T.Db.DutyAssignments.Where(a => a.DutyRosterShiftId == roster.Shifts[0].Id && !a.IsCancelled).Should().BeEmpty();
    }

    [Fact]
    public async Task Manager_Can_Cover_Or_Assign_As_Soon_As_The_Vacancy_Opens()
    {
        using var w = await World.Create(); var leave = await w.Leave(); await w.Approve(leave); var slot = w.T.Db.WorkOpenShifts.Single();
        w.Manager(); await w.S.ActOnOpenShiftAsync(slot.Id, new WorkRequestCommand { Revision = slot.Revision, Action = "ManagerCover", Reason = "Manager covers the shift" });
        slot.Status.Should().Be("Filled"); w.T.Db.WorkManagerCoverages.Should().ContainSingle();
        (await w.S.RostersAsync(w.T.Lot.Id)).Single().Shifts[0].Assigned.Should().Be(1);
        (await w.S.ProcessWorkRequestDeadlinesAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Approved_Original_Shift_Snapshot_Cannot_Be_Rewritten_Even_Without_A_Vacancy()
    {
        using var w = await World.Create(0); var leave = await w.Leave(); await w.Approve(leave); w.Manager();
        var roster = (await w.S.RostersAsync(w.T.Lot.Id)).Single();
        await FluentActions.Invoking(() => w.S.SaveShiftAsync(w.T.Lot.Id, roster.Id, roster.Shifts[0].Id,
            new RosterShiftInput { Revision = roster.Revision, Reason = "Try snapshot change", WorkDate = w.Day, WorkShiftTemplateId = roster.Shifts[0].WorkShiftTemplateId })).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Invoking(() => w.S.RemoveShiftAsync(w.T.Lot.Id, roster.Id, roster.Shifts[0].Id,
            new RosterMutationInput { Revision = roster.Revision, Reason = "Try removal" })).Should().ThrowAsync<BadRequestException>();
    }
}
