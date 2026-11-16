using System;
using System.Linq;
using System.Text.Json;
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
public class PayrollWorkflowTests
{
    private sealed class World : IDisposable
    {
        public OperationsTestContext T; public DatabaseContext Identity; public WorkSchedulingService Service;
        public DutyRosterModel Roster; public PeriodModel Period; public WorkShiftTemplate Template;
        public int AssignmentId => Roster.Shifts.Single().Assignments.Single().Id;
        public static async Task<World> Create(bool locked = true)
        {
            var t = await new OperationsTestContext().SeedAsync(); t.Clock.Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
            var identity = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, t.Claims);
            identity.Users.AddRange(new ApplicationUser { Id = t.StaffUserId, Role = "Staff", IsActive = true }, new ApplicationUser { Id = t.ManagerUserId, Role = "Manager", IsActive = true }, new ApplicationUser { Id = "admin", Role = "Admin", IsActive = true });
            await identity.SaveChangesAsync(); var s = new WorkSchedulingService(t.Db, identity, new LotScopeService(t.Db, t.Claims), t.Clock);
            t.ActAs(t.ManagerUserId, "Manager");
            var template = await s.SaveTemplateAsync(t.Lot.Id, null, new() { Name = "Frozen day", StartTime = new(8, 0), EndTime = new(16, 0), MinimumEntryStaff = 1 });
            var r = await s.CreateRosterAsync(t.Lot.Id, new() { Name = "Payroll evidence", FromDate = new(2026, 10, 30), ToDate = new(2026, 10, 30) });
            r = await s.AssignFromCalendarAsync(t.Lot.Id, r.Id, new() { Revision = r.Revision, WorkDate = r.FromDate, WorkShiftTemplateId = template.Id, EmployeeId = t.Db.Employees.Single(e => e.UserId == t.StaffUserId).Id, Position = "Entry" });
            r = await s.PublishAsync(t.Lot.Id, r.Id, new() { Revision = r.Revision });
            t.ActAs(t.StaffUserId, "Staff"); t.Clock.Now = new DateTimeOffset(r.Shifts.Single().StartsAt);
            var id = r.Shifts.Single().Assignments.Single().Id;
            await s.StartAssignmentAsync(id, new() { RosterRevision = r.Revision });
            t.Clock.Now = new DateTimeOffset(r.Shifts.Single().EndsAt); await s.EndAssignmentAsync(id, new() { Revision = 1, RosterRevision = r.Revision });
            t.Clock.Now = new(2026, 12, 2, 0, 0, 0, TimeSpan.Zero); t.ActAs("admin", "Admin");
            var period = await s.CreateAttendancePeriodAsync(new() { Name = "October locked", FromDate = new(2026, 10, 1), ToDate = new(2026, 10, 31), LotIds = [t.Lot.Id] });
            var p = PayrollCalculatorTests.StaffProfile(); p.UserId = t.StaffUserId;
            await s.SavePayrollProfileAsync(null, new() { UserId = p.UserId, Role = p.Role, EffectiveFrom = p.EffectiveFrom, HourlyRate = p.HourlyRate,
                NightStart = p.NightStart, NightEnd = p.NightEnd, NightFactor = p.NightFactor, HolidayFactor = p.HolidayFactor, OvertimeFactor = p.OvertimeFactor,
                StandbyDailyAllowance = p.StandbyDailyAllowance, Currency = p.Currency, MoneyDecimals = p.MoneyDecimals, RoundingMode = p.RoundingMode, Reason = "Arithmetic fixture, not a production default" });
            if (locked) await s.LockAttendancePeriodAsync(period.Id, new() { Revision = period.Revision, AcknowledgeUnconfirmed = true, Reason = "Verified fixture month" });
            return new() { T = t, Identity = identity, Service = s, Roster = r, Period = period, Template = template };
        }
        public void Admin() => T.ActAs("admin", "Admin");
        public PayrollGenerateInput Input => new() { ParkingLotId = T.Lot.Id, PayMonth = new(2026, 10, 1), Role = "Staff", Reason = "Verified arithmetic" };
        public async Task<PayrollSlip> Draft() => (await Service.GeneratePayrollAsync(Input)).Single();
        public async Task<PayrollSlip> Approve() { var p = await Draft(); return (await Service.ActOnPayrollAsync(p.Id, "approve", new() { Revision = p.Revision, Reason = "Checked 8 × 100 = 800" })).Slip; }
        public async Task<PayrollSlip> Pay() { var p = await Approve(); T.Clock.Advance(TimeSpan.FromSeconds(1)); return (await Service.ActOnPayrollAsync(p.Id, "paid", new() { Revision = p.Revision, Reason = "External transfer verified", PaidAt = T.Clock.Now, TransactionReference = "TEST-TRANSFER-001" })).Slip; }
        public void Dispose() { T.Db.Dispose(); Identity.Dispose(); }
    }
    [Fact] public async Task Calculation_Requires_Complete_Locked_Month_And_Sealed_Source()
    {
        using var w = await World.Create(false);
        await FluentActions.Invoking(() => w.Draft()).Should().ThrowAsync<BadRequestException>().WithMessage("*locked attendance*");
        (await w.Service.LockAttendancePeriodAsync(w.Period.Id, new() { Revision = 1, AcknowledgeUnconfirmed = true, Reason = "Verified" })).Status.Should().Be("Locked");
        w.T.Db.PayrollAttendanceSources.Should().ContainSingle();
        (await w.Draft()).Total.Should().Be(800);
    }
    [Fact] public async Task Template_Changes_Do_Not_Reprice_Frozen_Hours_And_Source_Links_Keep_Original_Ids()
    {
        using var w = await World.Create(); w.T.ActAs(w.T.ManagerUserId, "Manager");
        await w.Service.SaveTemplateAsync(w.T.Lot.Id, w.Template.Id, new() { Revision = w.Template.Revision, Name = "Completely changed later", StartTime = new(10, 0), EndTime = new(12, 0), IsActive = true });
        w.Admin(); var slip = await w.Draft(); slip.Total.Should().Be(800);
        var detail = await w.Service.PayrollSlipAsync(slip.Id); detail.Lines.Single().AssignmentId.Should().Be(w.AssignmentId);
        detail.Lines.Single().AttendanceRevision.Should().Be(2);
        var source = JsonSerializer.Serialize(await w.Service.PayrollLineSourceAsync(slip.Id, detail.Lines.Single().Id));
        source.Should().Contain("Frozen day").And.NotContain("Completely changed later");
    }
    [Fact] public async Task Repeated_Calculation_Is_Idempotent_And_Changed_Profile_Makes_Draft_Stale()
    {
        using var w = await World.Create(); var first = await w.Draft(); var second = await w.Draft();
        first.Id.Should().Be(second.Id); w.T.Db.PayrollLines.Should().ContainSingle();
        var p = (await w.Service.PayrollProfilesAsync(null)).Single();
        await w.Service.SavePayrollProfileAsync(p.Id, new() { UserId = p.UserId, Role = p.Role, EffectiveFrom = p.EffectiveFrom, Revision = p.Revision,
            HourlyRate = 200, NightStart = p.NightStart, NightEnd = p.NightEnd, NightFactor = p.NightFactor, HolidayFactor = p.HolidayFactor, OvertimeFactor = p.OvertimeFactor,
            Currency = p.Currency, MoneyDecimals = p.MoneyDecimals, RoundingMode = p.RoundingMode, Reason = "Correct uncommitted rate" });
        (await w.Service.PayrollSlipAsync(first.Id)).Stale.Should().BeTrue();
        await FluentActions.Invoking(() => w.Service.ActOnPayrollAsync(first.Id, "approve", new() { Revision = first.Revision, Reason = "Stale" })).Should().ThrowAsync<ConflictException>();
        (await w.Draft()).Total.Should().Be(1600); w.T.Db.PayrollAudits.Should().Contain(a => a.Action == "DraftCalculated" && a.BeforeJson != null);
    }
    [Fact] public async Task Only_Admin_Writes_And_Manager_Only_Sees_Approved_Staff_In_Their_Lot_With_Audit()
    {
        using var w = await World.Create(); var slip = await w.Draft(); w.T.ActAs(w.T.ManagerUserId, "Manager");
        await FluentActions.Invoking(() => w.Service.PayrollSlipAsync(slip.Id)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => w.Draft()).Should().ThrowAsync<ForbiddenException>();
        w.Admin(); await w.Approve(); w.T.ActAs(w.T.ManagerUserId, "Manager");
        (await w.Service.PayrollSlipAsync(slip.Id)).Slip.Total.Should().Be(800);
        w.T.Db.PayrollAudits.Should().Contain(a => a.Action == "PayslipViewed" && a.ActorId == w.T.ManagerUserId);
        w.T.ActAs("other-manager", "Manager"); await FluentActions.Invoking(() => w.Service.PayrollSlipAsync(slip.Id)).Should().ThrowAsync<ForbiddenException>();
        w.T.ActAs("other-staff", "Staff"); await FluentActions.Invoking(() => w.Service.PayrollSlipAsync(slip.Id)).Should().ThrowAsync<ForbiddenException>();
    }
    [Fact] public async Task Paid_Cannot_Be_Recalculated_Repayed_Or_Repriced_And_Payment_Metadata_Is_Required()
    {
        using var w = await World.Create(); var approved = await w.Approve();
        await FluentActions.Invoking(() => w.Service.ActOnPayrollAsync(approved.Id, "paid", new() { Revision = approved.Revision, Reason = "Missing transfer" })).Should().ThrowAsync<BadRequestException>();
        w.T.Clock.Advance(TimeSpan.FromSeconds(1));
        var paid = (await w.Service.ActOnPayrollAsync(approved.Id, "paid", new() { Revision = approved.Revision, Reason = "External transfer", PaidAt = w.T.Clock.Now, TransactionReference = "BANK-123" })).Slip;
        await FluentActions.Invoking(() => w.Draft()).Should().ThrowAsync<ConflictException>();
        await FluentActions.Invoking(() => w.Service.ActOnPayrollAsync(paid.Id, "paid", new() { Revision = paid.Revision, Reason = "Repeat", PaidAt = w.T.Clock.Now, TransactionReference = "BANK-123" })).Should().ThrowAsync<ConflictException>();
        var p = (await w.Service.PayrollProfilesAsync(null)).Single();
        await FluentActions.Invoking(() => w.Service.SavePayrollProfileAsync(p.Id, new() { UserId = p.UserId, Role = p.Role, EffectiveFrom = p.EffectiveFrom, Revision = p.Revision, HourlyRate = 300, Reason = "Retroactive" })).Should().ThrowAsync<ConflictException>();
        (await w.Service.PayrollSlipAsync(paid.Id)).Slip.TransactionReference.Should().Be("BANK-123");
    }
    [Fact] public async Task Complaint_Acceptance_Creates_One_Subsequent_Adjustment_And_Preserves_Paid_Slip()
    {
        using var w = await World.Create(); var paid = await w.Pay(); w.T.ActAs(w.T.StaffUserId, "Staff");
        var c = await w.Service.ComplainPayrollAsync(paid.Id, new() { Reason = "Overlooked approved item" });
        await FluentActions.Invoking(() => w.Service.ComplainPayrollAsync(paid.Id, new() { Reason = "Duplicate" })).Should().ThrowAsync<ConflictException>();
        w.Admin();
        var result = await w.Service.ResolvePayrollComplaintAsync(c.Id, new() { Revision = 1, Action = "Accept", Reason = "Evidence verified",
            Adjustment = new() { PayMonth = new(2026, 11, 1), Quantity = 1, UnitRate = 50, Reason = "Approved correction" } });
        result.Status.Should().Be("Accepted"); w.T.Db.PayrollAdjustments.Should().ContainSingle(a => a.SourceSlipId == paid.Id && a.SourceComplaintId == c.Id && a.UnitRate == 50);
        (await w.Service.PayrollSlipAsync(paid.Id)).Slip.Total.Should().Be(800);
        await FluentActions.Invoking(() => w.Service.ResolvePayrollComplaintAsync(c.Id, new() { Revision = 1, Action = "Accept", Reason = "Repeat" })).Should().ThrowAsync<ConflictException>();
    }
    [Fact] public async Task Admin_Profile_Is_Rejected_And_Arbitrary_Negative_Penalties_Are_Not_Adjustments()
    {
        using var w = await World.Create();
        await FluentActions.Invoking(() => w.Service.SavePayrollProfileAsync(null, new() { UserId = "admin", Role = "Admin", EffectiveFrom = new(2026, 1, 1), Reason = "No Admin salary" })).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Invoking(() => w.Service.AddPayrollAdjustmentAsync(new() { UserId = w.T.StaffUserId, Role = "Staff", ParkingLotId = w.T.Lot.Id,
            PayMonth = new(2026, 11, 1), Kind = "Adjustment", Quantity = -1, UnitRate = 10, Reason = "Untraced penalty" })).Should().ThrowAsync<BadRequestException>();
    }
    [Fact] public async Task Subsequent_Adjustment_Pays_An_Employee_With_No_New_Assignments_Without_Inventing_Hours()
    {
        using var w = await World.Create(); var paid = await w.Pay();
        var period = await w.Service.CreateAttendancePeriodAsync(new() { Name = "November no work", FromDate = new(2026, 11, 1), ToDate = new(2026, 11, 30), LotIds = [w.T.Lot.Id] });
        await w.Service.LockAttendancePeriodAsync(period.Id, new() { Revision = 1, AcknowledgeUnconfirmed = true, Reason = "Verified no November assignments" });
        await w.Service.AddPayrollAdjustmentAsync(new() { UserId = w.T.StaffUserId, Role = "Staff", ParkingLotId = w.T.Lot.Id, PayMonth = new(2026, 11, 1),
            Kind = "Adjustment", Quantity = 1, UnitRate = 25, SourceSlipId = paid.Id, Reason = "October correction" });
        var result = await w.Service.GeneratePayrollAsync(new() { ParkingLotId = w.T.Lot.Id, PayMonth = new(2026, 11, 1), Role = "Staff", Reason = "Pay correction only" });
        result.Single().Total.Should().Be(25);
        var detail = await w.Service.PayrollSlipAsync(result.Single().Id); detail.Lines.Should().ContainSingle(l => l.Kind == "Adjustment" && l.AssignmentId == null);
    }
    [Fact] public async Task Legacy_Source_Requires_Explicit_Unknown_Standby_Classification_And_Does_Not_Edit_Locked_Review()
    {
        using var w = await World.Create();
        var lot = w.T.Db.AttendancePeriodLots.Single(); var original = lot.SnapshotJson;
        w.T.Db.PayrollAttendanceSources.RemoveRange(w.T.Db.PayrollAttendanceSources);
        var standby = new DutyStandbyAssignment { DutyRosterId = w.Roster.Id, WorkDate = new(2026, 10, 30), EmployeeId = w.Roster.Shifts.Single().Assignments.Single().EmployeeId,
            Source = "Manual", IsCancelled = true, WasCalled = false };
        w.T.Db.DutyStandbyAssignments.Add(standby); await w.T.Db.SaveChangesAsync();
        await FluentActions.Invoking(() => w.Service.SealPayrollSourceAsync(lot.Id, new() { Reason = "Verify legacy", ManagerUserId = w.T.ManagerUserId })).Should().ThrowAsync<BadRequestException>().WithMessage("*Legacy standby*");
        await w.Service.SealPayrollSourceAsync(lot.Id, new() { Reason = "Admin verified earned standby from retained history", ManagerUserId = w.T.ManagerUserId, LegacyStandbyDecisions = new() { [standby.Id] = true } });
        lot.SnapshotJson.Should().Be(original); w.T.Db.PayrollAttendanceSources.Should().ContainSingle();
        (await w.Draft()).Total.Should().Be(830);
    }
    [Fact] public async Task Manager_Can_Read_Own_Published_Monthly_Slip_But_Not_Another_Manager_Even_At_The_Same_Lot()
    {
        using var w = await World.Create(); await w.Approve();
        var own = new PayrollSlip { UserId = w.T.ManagerUserId, EmployeeName = "Own Manager", Role = "Manager", ParkingLotId = w.T.Lot.Id, PayMonth = new(2026, 10, 1),
            Status = "Approved", Currency = "VND", Total = 2600, BasisHash = "fixture", BasisJson = "{}", SourceIdsJson = "[]", CreatedAt = w.T.Now, ApprovedAt = w.T.Now };
        var other = new PayrollSlip { UserId = "other-manager", EmployeeName = "Other Manager", Role = "Manager", ParkingLotId = w.T.Lot.Id, PayMonth = new(2026, 10, 1),
            Status = "Approved", Currency = "VND", Total = 5200, BasisHash = "fixture", BasisJson = "{}", SourceIdsJson = "[]", CreatedAt = w.T.Now, ApprovedAt = w.T.Now };
        w.T.Db.PayrollSlips.AddRange(own, other); await w.T.Db.SaveChangesAsync();
        w.T.ActAs(w.T.ManagerUserId, "Manager");
        (await w.Service.PayrollSlipsAsync(null, new(2026, 10, 1), true)).Should().ContainSingle(p => p.Id == own.Id);
        (await w.Service.PayrollSlipAsync(own.Id)).Slip.Total.Should().Be(2600);
        await FluentActions.Invoking(() => w.Service.PayrollSlipAsync(other.Id)).Should().ThrowAsync<ForbiddenException>();
    }
    [Fact] public async Task Explicit_Employee_Selection_Does_Not_Require_Unrelated_Manager_Profile_And_Does_Not_Alter_Hours()
    {
        using var w = await World.Create(); var input = w.Input; input.Role = null; input.UserId = w.T.StaffUserId;
        var rows = await w.Service.GeneratePayrollAsync(input); rows.Should().ContainSingle(s => s.Role == "Staff" && s.Total == 800);
        var detail = await w.Service.PayrollSlipAsync(rows.Single().Id); detail.Lines.Sum(l => l.Quantity).Should().Be(8);
    }
}
