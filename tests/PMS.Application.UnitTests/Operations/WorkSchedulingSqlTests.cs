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
            await db.Database.ExecuteSqlRawAsync("DROP TABLE dbo.DutyAttendanceEvents, dbo.DutyAttendances, dbo.DutyRosterChangeLogs, dbo.DutyAssignments, dbo.DutyRosterShifts, dbo.DutyRosters, dbo.WorkShiftTemplates");
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
            var template = await service.SaveTemplateAsync(lot.Id, null, new WorkShiftTemplateInput { Name = "Night", StartTime = new TimeOnly(22, 0), EndTime = new TimeOnly(6, 0), MinimumEntryStaff = 2 });
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
                try { await svc.SaveAssignmentAsync(lot.Id, r.Id, r.Shifts.Single().Id, null, new DutyAssignmentInput { Revision = r.Revision, EmployeeId = employee.Id, Position = "Entry" }); return true; }
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
                new DutyAssignmentInput { Revision = current.Revision, EmployeeId = employee.Id, Position = "Entry", Notes = "Saved handover", Reason = "Update handover" });
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
            await AutomatedMigration.EnsureWorkSchedulingTablesAsync(db);
            (await db.DutyAttendanceEvents.CountAsync()).Should().Be(2);
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
