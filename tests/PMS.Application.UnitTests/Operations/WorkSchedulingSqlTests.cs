using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using PMS.Application.Exceptions;
using PMS.Application.Models.WorkScheduling;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using PMS.Shared.Services;
using System.Security.Claims;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

public sealed class WorkSchedulingLocalDbFactAttribute : FactAttribute
{
    public WorkSchedulingLocalDbFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PMS_WORK_SCHEDULING_SQL_TESTS") != "1")
            Skip = "Set PMS_WORK_SCHEDULING_SQL_TESTS=1 on Windows with SQL LocalDB to run the isolated SQL test.";
    }
}

public class WorkSchedulingSqlTests
{
    [WorkSchedulingLocalDbFact]
    public async Task Migration_Is_Repeatable_Preserves_Legacy_Data_And_Serializes_Concurrent_Assignments()
    {
        // Only this generated database is ever created, altered or dropped.
        var name = $"PMS_WorkScheduling_Test_{Guid.NewGuid():N}";
        const string master = "Server=(localdb)\\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=15";
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand()) { create.CommandText = $"CREATE DATABASE [{name}]"; await create.ExecuteNonQueryAsync(); }
        var cs = new SqlConnectionStringBuilder(master) { InitialCatalog = name }.ConnectionString;
        var options = new DbContextOptionsBuilder<PmsDbContext>().UseSqlServer(cs).Options;
        var claims = Substitute.For<IClaimService>();
        claims.GetUserId().Returns("manager");
        claims.GetClaim(ClaimTypes.Role).Returns("Manager");
        try
        {
            await using var db = new PmsDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.Users ADD IsActive bit NOT NULL DEFAULT 1");
            var lot = new ParkingLot { Code = "SQL-TEST", Name = "SQL test", Address = "Test", Status = "Active", ManagerUserId = "manager", TimeZoneId = "SE Asia Standard Time" };
            db.ParkingLots.Add(lot);
            await db.SaveChangesAsync();
            var employee = new Employee { UserId = "staff", EmployeeCode = "SQL-STAFF", FullName = "SQL Staff", Position = "Attendant", Status = "Active", ParkingLotId = lot.Id };
            db.Employees.Add(employee);
            db.ShiftTemplates.Add(new ShiftTemplate { ParkingLotId = lot.Id, Name = "Legacy preserved", StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(16, 0), IsActive = true });
            await db.SaveChangesAsync();
            // Model-generated initial schema represents the existing tables; remove ONLY the empty new module tables
            // so the additive migration itself, rather than EnsureCreated, must create the module under test.
            await db.Database.ExecuteSqlRawAsync("DROP TABLE dbo.PayrollLines, dbo.PayrollComplaints, dbo.PayrollAdjustments, dbo.PayrollAudits, dbo.PayrollSlips, dbo.PayrollAttendanceSources, dbo.PayrollProfiles, dbo.AttendanceCarryAdjustments, dbo.AttendancePeriodEvents, dbo.AttendanceOvertimeDecisions, dbo.ManagerWorkDays, dbo.AttendancePeriodLots, dbo.AttendancePeriods, dbo.WorkManagerCoverages, dbo.WorkOpenShiftClaims, dbo.WorkOpenShifts, dbo.WorkLeaveDays, dbo.WorkLeaveBalances, dbo.WorkLeaveRequests, dbo.WorkSwapRequests, dbo.WorkScheduleWorkflowEvents, dbo.DutyStandbyAssignments, dbo.WorkRotationMembers, dbo.WorkRotationGroups, dbo.WorkRotationCycles, dbo.DutyAttendanceEvents, dbo.DutyAttendances, dbo.DutyRosterChangeLogs, dbo.DutyAssignments, dbo.DutyRosterShifts, dbo.DutyRosters, dbo.WorkShiftTemplates");
            await AutomatedMigration.EnsureWorkSchedulingTablesAsync(db);
            // Simulate upgrading the previous module version, which did not have assignment notes.
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.DutyAssignments DROP COLUMN Notes");
            await AutomatedMigration.EnsureWorkSchedulingTablesAsync(db);
            (await db.ShiftTemplates.SingleAsync()).Name.Should().Be("Legacy preserved");

            await using var identity = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseSqlServer(cs).Options, claims);
            identity.Users.Add(new ApplicationUser { Id = "staff", UserName = "staff", Role = "Staff", IsActive = true });
            await identity.SaveChangesAsync();
            var attendanceClock = new TestClock(new DateTimeOffset(2026, 11, 16, 14, 0, 0, TimeSpan.Zero));
            var service = new WorkSchedulingService(db, identity, new LotScopeService(db, claims), attendanceClock);
            var template = await service.SaveTemplateAsync(lot.Id, null, new WorkShiftTemplateInput { Name = "Night", StartTime = new TimeOnly(22, 0), EndTime = new TimeOnly(6, 0), MinimumStaff = 2 });
            var day = new DateOnly(2026, 11, 16);
            async Task<DutyRosterModel> MakeRoster()
            {
                var r = await service.CreateRosterAsync(lot.Id, new CreateDutyRosterInput { Name = "SQL roster", FromDate = day, ToDate = day });
                return await service.SaveShiftAsync(lot.Id, r.Id, null, new RosterShiftInput { Revision = r.Revision, WorkDate = day, WorkShiftTemplateId = template.Id });
            }
            var first = await MakeRoster(); var second = await MakeRoster();
            async Task<bool> Assign(DutyRosterModel r)
            {
                await using var writer = new PmsDbContext(options);
                await using var id = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseSqlServer(cs).Options, claims);
                var svc = new WorkSchedulingService(writer, id, new LotScopeService(writer, claims), attendanceClock);
                try { await svc.SaveAssignmentAsync(lot.Id, r.Id, r.Shifts.Single().Id, null, new DutyAssignmentInput { Revision = r.Revision, EmployeeId = employee.Id }); return true; }
                catch (BadRequestException) { return false; }
            }
            var outcomes = await Task.WhenAll(Assign(first), Assign(second));
            outcomes.Count(x => x).Should().Be(1, "the SQL application lock must make overlapping concurrent assignments mutually exclusive");
            var winner = outcomes[0] ? first : second;
            db.ChangeTracker.Clear();
            var current = (await service.RostersAsync(lot.Id)).Single(r => r.Id == winner.Id);
            current = await service.PublishAsync(lot.Id, current.Id, new RosterMutationInput { Revision = current.Revision });
            current.Status.Should().Be("Published");
            current.Shortages.Should().ContainSingle();
            (await db.UserNotifications.CountAsync()).Should().Be(1);
            (await db.DutyRosterChangeLogs.CountAsync(h => h.DutyRosterId == current.Id)).Should().Be(current.Revision);
            var assigned = current.Shifts.Single().Assignments.Single();
            current = await service.SaveAssignmentAsync(lot.Id, current.Id, current.Shifts.Single().Id, assigned.Id,
                new DutyAssignmentInput { Revision = current.Revision, EmployeeId = employee.Id, Notes = "Saved handover", Reason = "Update handover" });
            await AutomatedMigration.EnsureWorkSchedulingTablesAsync(db);
            db.ChangeTracker.Clear();
            (await db.DutyAssignments.SingleAsync()).Notes.Should().Be("Saved handover");
            (await db.DutyAssignments.SingleAsync()).Id.Should().Be(assigned.Id);
            (await db.ShiftTemplates.SingleAsync()).Name.Should().Be("Legacy preserved");
            attendanceClock.Now = new DateTimeOffset(2026, 11, 16, 14, 45, 0, TimeSpan.Zero);
            async Task<bool> Start()
            {
                var staffClaims = Substitute.For<IClaimService>();
                staffClaims.GetUserId().Returns("staff"); staffClaims.GetClaim(ClaimTypes.Role).Returns("Staff");
                await using var writer = new PmsDbContext(options);
                await using var users = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseSqlServer(cs).Options, staffClaims);
                var svc = new WorkSchedulingService(writer, users, new LotScopeService(writer, staffClaims), attendanceClock);
                try { await svc.StartAssignmentAsync(assigned.Id, new AttendanceMutationInput { Revision = 0, RosterRevision = current.Revision }); return true; }
                catch (ConflictException) { return false; }
            }
            var starts = await Task.WhenAll(Start(), Start());
            starts.Count(x => x).Should().Be(1);
            (await db.DutyAttendances.AsNoTracking().SingleAsync()).CheckInAt.Should().Be(attendanceClock.Now.UtcDateTime);
            attendanceClock.Now = new DateTimeOffset(current.Shifts.Single().EndsAt.AddMinutes(16));
            (await service.UpdateDueAttendanceAsync()).Should().Be(1);
            (await service.UpdateDueAttendanceAsync()).Should().Be(0);
            (await db.DutyAttendances.AsNoTracking().SingleAsync()).AutoEnded.Should().BeTrue();
            (await db.DutyAttendanceEvents.CountAsync()).Should().Be(2);
            var group = await service.SaveRotationGroupAsync(lot.Id, null, new RotationGroupInput { Name = "SQL A", Members = [new(employee.Id)] });
            var cycle = await service.SaveRotationCycleAsync(lot.Id, null, new RotationCycleInput { Name = "SQL cycle", Steps = [new(template.Id, 1)] });
            var rotation = await service.CreateRosterAsync(lot.Id, new CreateDutyRosterInput { Name = "SQL rotation", FromDate = new DateOnly(2026, 11, 18), ToDate = new DateOnly(2026, 11, 19) });
            var generate = new RotationGenerationInput { Revision = rotation.Revision, FromDate = rotation.FromDate, ToDate = rotation.ToDate,
                CycleStartDate = rotation.FromDate, CycleId = cycle.Id, GroupIds = [group.Id] };
            generate.PreviewToken = (await service.PreviewRotationAsync(lot.Id, rotation.Id, generate)).Token;
            async Task<bool> ApplyRotation()
            {
                await using var writer = new PmsDbContext(options);
                await using var users = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseSqlServer(cs).Options, claims);
                var svc = new WorkSchedulingService(writer, users, new LotScopeService(writer, claims), attendanceClock);
                try { await svc.ApplyRotationAsync(lot.Id, rotation.Id, generate); return true; }
                catch (ConflictException) { return false; }
            }
            (await Task.WhenAll(ApplyRotation(), ApplyRotation())).Count(x => x).Should().Be(1);
            await AutomatedMigration.EnsureWorkSchedulingTablesAsync(db);
            (await db.WorkRotationMembers.CountAsync()).Should().Be(1);
            (await db.WorkRotationCycles.CountAsync()).Should().Be(1);
            (await db.DutyAssignments.CountAsync(a => a.Source == "Rotation" && !a.IsCancelled)).Should().Be(2);
            identity.Users.AddRange(new ApplicationUser { Id = "claim-a", UserName = "claim-a", Role = "Staff", IsActive = true }, new ApplicationUser { Id = "claim-b", UserName = "claim-b", Role = "Staff", IsActive = true });
            await identity.SaveChangesAsync();
            var claimantA = new Employee { UserId = "claim-a", EmployeeCode = "CLAIM-A", FullName = "Claim A", Position = "Staff", Status = "Active", ParkingLotId = lot.Id };
            var claimantB = new Employee { UserId = "claim-b", EmployeeCode = "CLAIM-B", FullName = "Claim B", Position = "Staff", Status = "Active", ParkingLotId = lot.Id };
            db.Employees.AddRange(claimantA, claimantB); await db.SaveChangesAsync();
            var requestRoster = await service.CreateRosterAsync(lot.Id, new CreateDutyRosterInput { Name = "SQL requests", FromDate = new DateOnly(2026, 11, 23), ToDate = new DateOnly(2026, 11, 23) });
            requestRoster = await service.AssignFromCalendarAsync(lot.Id, requestRoster.Id, new CalendarAssignmentInput { Revision = requestRoster.Revision, WorkDate = requestRoster.FromDate, WorkShiftTemplateId = template.Id, EmployeeId = employee.Id });
            requestRoster = await service.PublishAsync(lot.Id, requestRoster.Id, new RosterMutationInput { Revision = requestRoster.Revision });
            var originalId = requestRoster.Shifts.Single().Assignments.Single().Id;
            claims.GetUserId().Returns("staff"); claims.GetClaim(ClaimTypes.Role).Returns("Staff");
            var leave = await service.CreateLeaveAsync(new CreateLeaveInput { AssignmentId = originalId, Kind = "Planned", Reason = "SQL leave" });
            claims.GetUserId().Returns("manager"); claims.GetClaim(ClaimTypes.Role).Returns("Manager");
            await service.DecideLeaveAsync(leave.Id, new WorkRequestCommand { Revision = leave.Revision, Action = "Approve", Reason = "SQL approved" });
            var slot = await db.WorkOpenShifts.SingleAsync(s => s.OriginalAssignmentId == originalId);
            foreach (var user in new[] { "claim-a", "claim-b" }) {
                claims.GetUserId().Returns(user); claims.GetClaim(ClaimTypes.Role).Returns("Staff");
                await service.ActOnOpenShiftAsync(slot.Id, new WorkRequestCommand { Revision = slot.Revision, Action = "Claim" });
            }
            var claimIds = await db.WorkOpenShiftClaims.Where(c => c.WorkOpenShiftId == slot.Id).OrderBy(c => c.Id).Select(c => c.Id).ToListAsync();
            var slotRevision = slot.Revision;
            async Task<bool> Confirm(int claimId)
            {
                var managerClaims = Substitute.For<IClaimService>(); managerClaims.GetUserId().Returns("manager"); managerClaims.GetClaim(ClaimTypes.Role).Returns("Manager");
                await using var writer = new PmsDbContext(options);
                await using var users = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseSqlServer(cs).Options, managerClaims);
                var svc = new WorkSchedulingService(writer, users, new LotScopeService(writer, managerClaims), attendanceClock);
                try { await svc.ActOnOpenShiftAsync(slot.Id, new WorkRequestCommand { Revision = slotRevision, ClaimId = claimId, Action = "Confirm", Reason = "SQL race" }); return true; }
                catch (ConflictException) { return false; }
            }
            (await Task.WhenAll(Confirm(claimIds[0]), Confirm(claimIds[1]))).Count(x => x).Should().Be(1);
            (await db.DutyAssignments.CountAsync(a => a.OriginalAssignmentId == originalId && !a.IsCancelled)).Should().Be(1);
            (await db.WorkOpenShiftClaims.CountAsync(c => c.WorkOpenShiftId == slot.Id && c.Status == "Confirmed")).Should().Be(1);
            await AutomatedMigration.EnsureWorkSchedulingTablesAsync(db);
            (await db.WorkLeaveDays.CountAsync()).Should().Be(1);
            (await db.DutyAttendanceEvents.CountAsync()).Should().Be(2);
            await AutomatedMigration.EnsureWorkSchedulingTablesAsync(db);
            (await db.DutyAttendanceEvents.CountAsync()).Should().Be(2);
            db.ChangeTracker.Clear();
            claims.GetUserId().Returns("admin"); claims.GetClaim(ClaimTypes.Role).Returns("Admin");
            var period = await service.CreateAttendancePeriodAsync(new CreateAttendancePeriodInput {
                Name = "SQL attendance lock", FromDate = day, ToDate = day, LotIds = [lot.Id] });
            claims.GetUserId().Returns("manager"); claims.GetClaim(ClaimTypes.Role).Returns("Manager");
            var detail = await service.AttendanceDetailAsync(assigned.Id);
            detail.PayReview.OriginalCheckInAt.Should().Be(new DateTime(2026, 11, 16, 14, 45, 0, DateTimeKind.Utc));
            detail = await service.ReviewAssignmentAsync(assigned.Id, new AttendanceMutationInput {
                Revision = detail.Attendance.Revision, RosterRevision = current.Revision, Reason = "SQL reviewed automatic checkout" });
            await service.DecideOvertimeAsync(assigned.Id, new OvertimeDecisionInput {
                Revision = detail.Attendance.Revision, RosterRevision = current.Revision, Approved = false, Reason = "Early start not approved" });
            var review = await service.ReviewPeriodAsync(period.Id, lot.Id);
            review.Employees.Single().ScheduledHours.Should().Be(2);
            review.Warnings.Should().BeEmpty();
            await service.ConfirmAttendancePeriodAsync(period.Id, lot.Id, new PeriodActionInput { Revision = review.Lot.Revision, Reason = "SQL verified hours" });
            async Task<bool> LockPeriod()
            {
                var adminClaims = Substitute.For<IClaimService>(); adminClaims.GetUserId().Returns("admin"); adminClaims.GetClaim(ClaimTypes.Role).Returns("Admin");
                await using var writer = new PmsDbContext(options);
                await using var users = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseSqlServer(cs).Options, adminClaims);
                var svc = new WorkSchedulingService(writer, users, new LotScopeService(writer, adminClaims), attendanceClock);
                try { await svc.LockAttendancePeriodAsync(period.Id, new() { Revision = period.Revision, Reason = "Concurrent close" }); return true; }
                catch (ConflictException) { return false; }
            }
            (await Task.WhenAll(LockPeriod(), LockPeriod())).Count(x => x).Should().Be(1);
            db.ChangeTracker.Clear();
            var lockedReview = await service.ReviewPeriodAsync(period.Id, lot.Id);
            lockedReview.Lot.Status.Should().Be("Locked"); lockedReview.Employees.Single().ApprovedHours.Should().Be(2);
            await FluentActions.Invoking(() => service.AdjustAttendanceAsync(assigned.Id, new() {
                Revision = detail.Attendance.Revision, RosterRevision = current.Revision, Reason = "Locked correction attempt",
                CheckInAt = new DateTimeOffset(current.Shifts.Single().StartsAt), CheckOutAt = new DateTimeOffset(current.Shifts.Single().EndsAt) })).Should().ThrowAsync<ConflictException>();
            db.ChangeTracker.Clear();
            await AutomatedMigration.EnsureWorkSchedulingTablesAsync(db);
            (await db.AttendancePeriodLots.SingleAsync()).Status.Should().Be("Locked");
            (await db.ShiftTemplates.SingleAsync()).Name.Should().Be("Legacy preserved");
            // Complete a payroll month from several independently locked attendance periods.
            // Explicit fixture rates only: night 8h × 100 × 1.5 + paid leave 8h × 100 = 2000.
            attendanceClock.Now = new DateTimeOffset(2026, 12, 2, 0, 0, 0, TimeSpan.Zero);
            claims.GetUserId().Returns("admin"); claims.GetClaim(ClaimTypes.Role).Returns("Admin");
            foreach (var range in new[] { (From: new DateOnly(2026, 11, 1), To: new DateOnly(2026, 11, 15)), (From: new DateOnly(2026, 11, 17), To: new DateOnly(2026, 11, 30)) }) {
                var rest = await service.CreateAttendancePeriodAsync(new() { Name = "SQL remaining attendance", FromDate = range.From, ToDate = range.To, LotIds = [lot.Id] });
                await service.LockAttendancePeriodAsync(rest.Id, new() { Revision = rest.Revision, AcknowledgeUnconfirmed = true, Reason = "SQL monthly payroll evidence" });
            }
            foreach (var userId in new[] { "staff", "claim-a", "claim-b" })
                await service.SavePayrollProfileAsync(null, new() { UserId = userId, Role = "Staff", EffectiveFrom = new(2026, 11, 1), HourlyRate = 100,
                    NightStart = new(22, 0), NightEnd = new(6, 0), NightFactor = 1.5m, HolidayFactor = 2, OvertimeFactor = 1.25m,
                    StandbyDailyAllowance = 30, Currency = "VND", MoneyDecimals = 2, RoundingMode = "AwayFromZero", Reason = "Explicit SQL arithmetic fixture" });
            var payroll = await service.GeneratePayrollAsync(new() { ParkingLotId = lot.Id, PayMonth = new(2026, 11, 1), Role = "Staff", Reason = "SQL 2000 arithmetic check" });
            var staffSlip = payroll.Single(s => s.UserId == "staff"); staffSlip.Total.Should().Be(2000);
            var repeatedPayroll = await service.GeneratePayrollAsync(new() { ParkingLotId = lot.Id, PayMonth = new(2026, 11, 1), Role = "Staff", Reason = "Idempotent SQL retry" });
            repeatedPayroll.Single(s => s.UserId == "staff").Id.Should().Be(staffSlip.Id);
            async Task<bool> PayrollAction(string action, int revision)
            {
                var actor = Substitute.For<IClaimService>(); actor.GetUserId().Returns("admin"); actor.GetClaim(ClaimTypes.Role).Returns("Admin");
                await using var writer = new PmsDbContext(options);
                await using var users = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseSqlServer(cs).Options, actor);
                var svc = new WorkSchedulingService(writer, users, new LotScopeService(writer, actor), attendanceClock);
                try { await svc.ActOnPayrollAsync(staffSlip.Id, action, new() { Revision = revision, Reason = "SQL concurrent payroll action", PaidAt = attendanceClock.Now, TransactionReference = "SQL-EXTERNAL-TRANSFER" }); return true; }
                catch (ConflictException) { return false; }
            }
            (await Task.WhenAll(PayrollAction("approve", 1), PayrollAction("approve", 1))).Count(x => x).Should().Be(1);
            attendanceClock.Advance(TimeSpan.FromSeconds(1));
            (await Task.WhenAll(PayrollAction("paid", 2), PayrollAction("paid", 2))).Count(x => x).Should().Be(1);
            db.ChangeTracker.Clear(); await AutomatedMigration.EnsureWorkSchedulingTablesAsync(db);
            var paidSlip = await db.PayrollSlips.SingleAsync(s => s.Id == staffSlip.Id);
            paidSlip.Status.Should().Be("Paid"); paidSlip.Total.Should().Be(2000);
            (await db.PayrollLines.Where(l => l.PayrollSlipId == paidSlip.Id).SumAsync(l => l.Amount)).Should().Be(2000);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]";
            await drop.ExecuteNonQueryAsync();
        }
    }
}
