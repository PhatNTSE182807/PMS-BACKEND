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
public class AttendancePeriodTests
{
    private sealed class World : IDisposable
    {
        public OperationsTestContext T;
        public DatabaseContext Identity;
        public WorkSchedulingService Service;
        public DutyRosterModel Roster;
        public PeriodModel October, November;
        public int Id => Roster.Shifts.Single().Assignments.Single().Id;
        public DateTime Start => Roster.Shifts.Single().StartsAt;
        public static async Task<World> Create(int overtime = 0, bool finish = true, bool start = true)
        {
            var t = await new OperationsTestContext().SeedAsync();
            t.Clock.Now = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
            var identity = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, t.Claims);
            identity.Users.Add(new ApplicationUser { Id = t.StaffUserId, Role = "Staff", IsActive = true });
            identity.Users.Add(new ApplicationUser { Id = t.ManagerUserId, Role = "Manager", IsActive = true });
            await identity.SaveChangesAsync();
            var s = new WorkSchedulingService(t.Db, identity, new LotScopeService(t.Db, t.Claims), t.Clock);
            t.ActAs(t.ManagerUserId, "Manager");
            var template = await s.SaveTemplateAsync(t.Lot.Id, null, new WorkShiftTemplateInput { Name = "Night", StartTime = new TimeOnly(22, 0), EndTime = new TimeOnly(6, 0), MinimumStaff = 1 });
            var day = new DateOnly(2026, 10, 31);
            var r = await s.CreateRosterAsync(t.Lot.Id, new CreateDutyRosterInput { Name = "Boundary", FromDate = day, ToDate = day.AddDays(1) });
            r = await s.AssignFromCalendarAsync(t.Lot.Id, r.Id, new CalendarAssignmentInput { Revision = r.Revision, WorkDate = day, WorkShiftTemplateId = template.Id, EmployeeId = t.Db.Employees.Single(e => e.UserId == t.StaffUserId).Id });
            r = await s.PublishAsync(t.Lot.Id, r.Id, new RosterMutationInput { Revision = r.Revision });
            var w = new World { T = t, Identity = identity, Service = s, Roster = r };
            t.ActAs("admin", "Admin");
            w.October = await s.CreateAttendancePeriodAsync(new CreateAttendancePeriodInput { Name = "October", Month = new(2026, 10, 1), LotIds = [t.Lot.Id] });
            w.November = await s.CreateAttendancePeriodAsync(new CreateAttendancePeriodInput { Name = "November", Month = new(2026, 11, 1), LotIds = [t.Lot.Id] });
            t.ActAs(t.StaffUserId, "Staff"); t.Clock.Now = new DateTimeOffset(w.Start);
            if (start) await s.StartAssignmentAsync(w.Id, w.Command());
            if (start && finish) {
                t.Clock.Now = new DateTimeOffset(r.Shifts.Single().EndsAt.AddMinutes(overtime));
                await s.EndAssignmentAsync(w.Id, w.Command(1));
            }
            t.Clock.Now = new DateTimeOffset(2026, 12, 2, 0, 0, 0, TimeSpan.Zero); w.Manager();
            return w;
        }
        public void Manager() => T.ActAs(T.ManagerUserId, "Manager");
        public void Admin() => T.ActAs("admin", "Admin");
        public AttendanceMutationInput Command(int revision = 0) => new() { Revision = revision, RosterRevision = Roster.Revision, Reason = "Verified evidence" };
        public Task<AttendancePeriodReview> Review(PeriodModel p) => Service.ReviewPeriodAsync(p.Id, T.Lot.Id);
        public async Task Confirm(PeriodModel p) { Manager(); var v = await Review(p); await Service.ConfirmAttendancePeriodAsync(p.Id, T.Lot.Id, new() { Revision = v.Lot.Revision, Reason = "Reviewed all staff" }); }
        public async Task Lock(PeriodModel p) { Admin(); await Service.LockAttendancePeriodAsync(p.Id, new() { Revision = p.Revision, Reason = "Close period" }); }
        public void Dispose() { T.Db.Dispose(); Identity.Dispose(); }
    }

    [Fact]
    public async Task Overnight_Hours_Are_Split_At_Local_Midnight_And_Source_Is_Preserved()
    {
        using var w = await World.Create();
        var oct = await w.Review(w.October); var nov = await w.Review(w.November);
        oct.Employees.Single().ScheduledHours.Should().Be(2); nov.Employees.Single().ScheduledHours.Should().Be(6);
        oct.Employees.Single().ActualHours.Should().Be(2); nov.Employees.Single().ActualHours.Should().Be(6);
        oct.Rows.Single().StartsAt.Should().Be(nov.Rows.Single().StartsAt);
        oct.Rows.Single().AssignmentId.Should().Be(nov.Rows.Single().AssignmentId);
        oct.Employees.Single().ApprovedHours.Should().Be(0);
        await w.Confirm(w.October); await w.Confirm(w.November);
        (await w.Review(w.October)).Employees.Single().ApprovedHours.Should().Be(2);
        (await w.Review(w.November)).Employees.Single().ApprovedHours.Should().Be(6);
    }

    [Fact]
    public async Task Overnight_Absence_Is_Counted_Once_In_The_Start_Date_Period()
    {
        using var w = await World.Create(start: false);
        await w.Service.UpdateDueAttendanceAsync();
        (await w.Review(w.October)).Employees.Single().AbsentShifts.Should().Be(1);
        (await w.Review(w.November)).Employees.Single().AbsentShifts.Should().Be(0);
        (await w.Review(w.October)).Employees.Single().ApprovedHours.Should().Be(0);
    }

    [Fact]
    public async Task Correction_Invalidates_Only_Affected_Confirmations_And_Preserves_Original_Evidence()
    {
        using var w = await World.Create(); await w.Confirm(w.October); await w.Confirm(w.November);
        await w.Service.AdjustAttendanceAsync(w.Id, new() { Revision = 2, RosterRevision = w.Roster.Revision, Reason = "Camera evidence", CheckInAt = new DateTimeOffset(w.Start.AddMinutes(30)), CheckOutAt = new DateTimeOffset(w.Start.AddHours(8)) });
        var oct = await w.Review(w.October); var nov = await w.Review(w.November);
        oct.Lot.Status.Should().Be("Open"); nov.Lot.Status.Should().Be("Open");
        oct.Employees.Single().ActualHours.Should().Be(2); oct.Employees.Single().AdjustedHours.Should().Be(1.5m);
        w.T.Db.AttendancePeriodEvents.Count(x => x.Action == "ConfirmationInvalidated").Should().Be(2);
    }

    [Fact]
    public async Task Locked_Part_Of_Overnight_Assignment_Blocks_All_Direct_Time_And_Overtime_Changes()
    {
        using var w = await World.Create(); await w.Confirm(w.October); await w.Lock(w.October); w.Manager();
        await FluentActions.Invoking(() => w.Service.AdjustAttendanceAsync(w.Id, new() { Revision = 2, RosterRevision = w.Roster.Revision, Reason = "move outside locked period", CheckInAt = new DateTimeOffset(w.Start.AddDays(1)), CheckOutAt = new DateTimeOffset(w.Start.AddDays(1).AddHours(8)) })).Should().ThrowAsync<ConflictException>();
        w.T.Db.ChangeTracker.Clear();
        (await w.Service.AttendanceDetailAsync(w.Id)).PayReview.Locked.Should().BeTrue();
        (await w.Review(w.October)).Employees.Single().ApprovedHours.Should().Be(2);
    }

    [Fact]
    public async Task Published_Schedule_Note_Edit_Invalidates_Confirmation_And_Is_Rejected_After_Lock()
    {
        using var w = await World.Create(); await w.Confirm(w.October);
        var shift = w.Roster.Shifts.Single(); var a = shift.Assignments.Single();
        w.Roster = await w.Service.SaveAssignmentAsync(w.T.Lot.Id, w.Roster.Id, shift.Id, a.Id, new() { Revision = w.Roster.Revision, EmployeeId = a.EmployeeId, Notes = "Review change", Reason = "Explain update" });
        (await w.Review(w.October)).Lot.Status.Should().Be("Open");
        await w.Confirm(w.October); await w.Lock(w.October); w.Manager();
        await FluentActions.Invoking(() => w.Service.SaveAssignmentAsync(w.T.Lot.Id, w.Roster.Id, shift.Id, a.Id, new() { Revision = w.Roster.Revision, EmployeeId = a.EmployeeId, Notes = "Locked change", Reason = "Explain" })).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Overtime_Needs_Explicit_Decision_And_Correction_Makes_Decision_Stale()
    {
        using var w = await World.Create(10);
        (await w.Review(w.November)).Warnings.Should().Contain(x => x.Contains("Overtime"));
        var detail = await w.Service.DecideOvertimeAsync(w.Id, new() { Revision = 2, RosterRevision = w.Roster.Revision, Reason = "Approved handover", Approved = true });
        detail.PayReview.OvertimeStatus.Should().Be("Approved");
        await w.Confirm(w.November);
        (await w.Review(w.November)).Employees.Single().ApprovedHours.Should().BeApproximately(6m + 1m / 6, .00001m);
        await w.Service.AdjustAttendanceAsync(w.Id, new() { Revision = 2, RosterRevision = w.Roster.Revision, Reason = "Correct end", CheckInAt = new DateTimeOffset(w.Start), CheckOutAt = new DateTimeOffset(w.Start.AddHours(8).AddMinutes(5)) });
        (await w.Review(w.November)).Rows.Single().Pay.OvertimeStatus.Should().Be("Pending");
    }

    [Fact]
    public async Task AutoEnded_Assignment_Must_Be_Reviewed_And_Jobs_Are_Idempotent_After_Lock()
    {
        using var w = await World.Create(finish: false);
        await w.Service.UpdateDueAttendanceAsync();
        (await w.Review(w.October)).Warnings.Should().Contain(x => x.Contains("Auto-ended"));
        await FluentActions.Invoking(() => w.Confirm(w.October)).Should().ThrowAsync<BadRequestException>();
        await w.Service.ReviewAssignmentAsync(w.Id, w.Command(2)); await w.Confirm(w.October); await w.Lock(w.October);
        (await w.Service.UpdateDueAttendanceAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Admin_Cannot_Confirm_Manager_Cannot_Lock_Staff_Cannot_Read_Review()
    {
        using var w = await World.Create();
        await FluentActions.Invoking(() => w.Service.LockAttendancePeriodAsync(w.October.Id, new())).Should().ThrowAsync<ForbiddenException>();
        w.Admin(); await FluentActions.Invoking(() => w.Service.ConfirmAttendancePeriodAsync(w.October.Id, w.T.Lot.Id, new())).Should().ThrowAsync<ForbiddenException>();
        w.T.ActAs(w.T.StaffUserId, "Staff");
        await FluentActions.Invoking(() => w.Review(w.October)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => w.Service.DecideOvertimeAsync(w.Id, new())).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Unconfirmed_Lot_Requires_Explicit_Admin_Acknowledgment_And_Double_Lock_Is_Rejected()
    {
        using var w = await World.Create(); w.Admin();
        await FluentActions.Invoking(() => w.Lock(w.October)).Should().ThrowAsync<ConflictException>();
        await w.Service.LockAttendancePeriodAsync(w.October.Id, new() { Revision = 1, Reason = "Emergency close after audit", AcknowledgeUnconfirmed = true });
        await FluentActions.Invoking(() => w.Service.LockAttendancePeriodAsync(w.October.Id, new() { Revision = 2, Reason = "Repeat", AcknowledgeUnconfirmed = true })).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Carry_Adjustment_Preserves_Locked_Snapshot_And_Invalidates_Target_Confirmation()
    {
        using var w = await World.Create(); await w.Confirm(w.October); await w.Lock(w.October); await w.Confirm(w.November);
        var source = await w.Review(w.October); var target = await w.Review(w.November);
        await w.Service.AddCarryAdjustmentAsync(target.Lot.Id, new() { Revision = target.Lot.Revision, SourcePeriodLotId = source.Lot.Id, EmployeeId = source.Employees.Single().EmployeeId, AssignmentId = w.Id, Hours = .5m, Reason = "Late evidence found" });
        (await w.Review(w.October)).Employees.Single().ApprovedHours.Should().Be(2);
        (await w.Review(w.November)).Lot.Status.Should().Be("Open");
        (await w.Review(w.November)).Employees.Single().CarryHours.Should().Be(.5m);
    }

    [Fact]
    public async Task Periods_Cannot_Overlap_And_Manager_Workdays_Are_Admin_Only_And_Locked()
    {
        using var w = await World.Create(); w.Admin();
        await FluentActions.Invoking(() => w.Service.CreateAttendancePeriodAsync(new() { Name = "Same month", Month = new(2026, 10, 1), LotIds = [w.T.Lot.Id] })).Should().ThrowAsync<ConflictException>();
        await w.Service.SaveManagerWorkDayAsync(w.T.Lot.Id, new() { ManagerUserId = w.T.ManagerUserId, WorkDate = new(2026, 10, 31), Kind = "Work", Reason = "Admin verified" });
        await w.Confirm(w.October); await w.Lock(w.October);
        (await w.Review(w.October)).ManagerDays.Should().ContainSingle(x => x.Kind == "Work" && x.WorkDate == new DateOnly(2026, 10, 31));
        await FluentActions.Invoking(() => w.Service.SaveManagerWorkDayAsync(w.T.Lot.Id, new() { ManagerUserId = w.T.ManagerUserId, WorkDate = new(2026, 10, 31), Kind = "Leave", Revision = 1, Reason = "change locked day" })).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Weekly_Excess_Is_Overtime_And_Changing_Earlier_Hours_Invalidates_Later_Period_Approval()
    {
        using var w = await World.Create();
        var employeeId = w.Roster.Shifts.Single().Assignments.Single().EmployeeId;
        var templateId = w.Roster.Shifts.Single().WorkShiftTemplateId;
        var earlier = new DutyRoster { ParkingLotId = w.T.Lot.Id, Name = "Earlier week", FromDate = new(2026, 10, 26), ToDate = new(2026, 10, 31),
            Status = "Published", PublishedAt = w.Start.AddDays(-10), CreatedAt = w.Start.AddDays(-10), CreatedBy = w.T.ManagerUserId };
        w.T.Db.DutyRosters.Add(earlier); await w.T.Db.SaveChangesAsync();
        var firstId = 0;
        for (var i = 0; i < 6; i++) {
            var start = w.Start.AddDays(i - 5).AddHours(-14); // Monday–Saturday, 08:00–16:00 local.
            var shift = new DutyRosterShift { DutyRosterId = earlier.Id, WorkShiftTemplateId = templateId, Name = "Day", TimeZoneId = w.Roster.Shifts.Single().TimeZoneId,
                WorkDate = new DateOnly(2026, 10, 26).AddDays(i), StartTime = new(8, 0), EndTime = new(16, 0), StartsAt = start, EndsAt = start.AddHours(8) };
            w.T.Db.DutyRosterShifts.Add(shift); await w.T.Db.SaveChangesAsync();
            var assignment = new DutyAssignment { DutyRosterShiftId = shift.Id, EmployeeId = employeeId, AttendanceTrackingFrom = start.AddDays(-10) };
            w.T.Db.DutyAssignments.Add(assignment); await w.T.Db.SaveChangesAsync();
            w.T.Db.DutyAttendances.Add(new DutyAttendance { DutyAssignmentId = assignment.Id, CheckInAt = start, CheckOutAt = start.AddHours(8), Revision = 2 });
            await w.T.Db.SaveChangesAsync(); if (i == 0) firstId = assignment.Id;
        }
        var detail = await w.Service.AttendanceDetailAsync(w.Id);
        detail.PayReview.RegularHours.Should().Be(0); detail.PayReview.OvertimeHours.Should().Be(8);
        await w.Service.DecideOvertimeAsync(w.Id, new() { Revision = 2, RosterRevision = w.Roster.Revision, Approved = true, Reason = "Weekly coverage approved" });
        await w.Confirm(w.November);
        var first = await w.Service.AttendanceDetailAsync(firstId);
        await w.Service.AdjustAttendanceAsync(firstId, new() { Revision = 2, RosterRevision = earlier.Revision,
            CheckInAt = first.Attendance.CheckInAt, CheckOutAt = first.Attendance.CheckOutAt?.AddHours(-1), Reason = "Earlier hour corrected" });
        (await w.Review(w.November)).Lot.Status.Should().Be("Open");
        detail = await w.Service.AttendanceDetailAsync(w.Id);
        detail.PayReview.RegularHours.Should().Be(1); detail.PayReview.OvertimeHours.Should().Be(7);
        detail.PayReview.OvertimeStatus.Should().Be("Pending");
        await w.Service.DecideOvertimeAsync(w.Id, new() { Revision = 2, RosterRevision = w.Roster.Revision, Approved = true, Reason = "Reapproved changed weekly evidence" });
        await w.Confirm(w.November); await w.Lock(w.November); w.Manager();
        await FluentActions.Invoking(() => w.Service.AdjustAttendanceAsync(firstId, new() { Revision = 3, RosterRevision = earlier.Revision,
            CheckInAt = first.Attendance.CheckInAt, CheckOutAt = first.Attendance.CheckOutAt?.AddHours(-2), Reason = "Cannot alter locked dependent hours" })).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Individual_Review_Materializes_Delayed_Automatic_End_Before_Locking()
    {
        using var w = await World.Create(finish: false);
        var detail = await w.Service.ReviewAssignmentAsync(w.Id, w.Command(1));
        detail.Attendance.AutoEnded.Should().BeTrue();
        w.T.Db.DutyAttendances.Single().CheckOutAt.Should().Be(w.Start.AddHours(8));
        await w.Confirm(w.October); await w.Lock(w.October);
        (await w.Service.UpdateDueAttendanceAsync()).Should().Be(0);
    }
}
