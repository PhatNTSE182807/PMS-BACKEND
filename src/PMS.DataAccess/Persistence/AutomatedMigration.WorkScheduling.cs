using Microsoft.EntityFrameworkCore;

namespace PMS.DataAccess.Persistence;

public static partial class AutomatedMigration
{
    // Additive and repeatable. No references to or changes to the legacy Shift* tables.
    public static Task EnsureWorkSchedulingTablesAsync(PmsDbContext db) => db.Database.ExecuteSqlRawAsync(SqlWorkScheduling);

    public const string SqlWorkScheduling = """
        IF OBJECT_ID('dbo.WorkShiftTemplates', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.WorkShiftTemplates (
                Id int IDENTITY PRIMARY KEY, ParkingLotId int NOT NULL,
                Name nvarchar(100) NOT NULL, StartTime time NOT NULL, EndTime time NOT NULL,
                MinimumStaff int NOT NULL,
                IsActive bit NOT NULL, Revision int NOT NULL,
                CONSTRAINT FK_WorkShiftTemplates_Lot FOREIGN KEY (ParkingLotId) REFERENCES dbo.ParkingLots(Id),
                CONSTRAINT CK_WorkShiftTemplates_Hours CHECK (StartTime <> EndTime),
                CONSTRAINT CK_WorkShiftTemplates_MinimumStaff CHECK (MinimumStaff >= 0)
            );
            CREATE INDEX IX_WorkShiftTemplates_ParkingLotId_IsActive ON dbo.WorkShiftTemplates(ParkingLotId, IsActive);
        END;
        IF OBJECT_ID('dbo.DutyRosters', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.DutyRosters (
                Id int IDENTITY PRIMARY KEY, ParkingLotId int NOT NULL, Name nvarchar(100) NOT NULL,
                FromDate date NOT NULL, ToDate date NOT NULL, Status varchar(20) NOT NULL,
                Revision int NOT NULL, CreatedBy nvarchar(450) NOT NULL, CreatedAt datetime2(3) NOT NULL,
                PublishedAt datetime2(3) NULL,
                CONSTRAINT FK_DutyRosters_Lot FOREIGN KEY (ParkingLotId) REFERENCES dbo.ParkingLots(Id),
                CONSTRAINT CK_DutyRosters_Dates CHECK (FromDate <= ToDate),
                CONSTRAINT CK_DutyRosters_Status CHECK (Status IN ('Draft', 'Published'))
            );
            CREATE INDEX IX_DutyRosters_ParkingLotId_FromDate_ToDate ON dbo.DutyRosters(ParkingLotId, FromDate, ToDate);
        END;
        IF OBJECT_ID('dbo.DutyRosterShifts', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.DutyRosterShifts (
                Id int IDENTITY PRIMARY KEY, DutyRosterId int NOT NULL, WorkShiftTemplateId int NOT NULL,
                WorkDate date NOT NULL, Name nvarchar(100) NOT NULL, TimeZoneId nvarchar(100) NOT NULL,
                StartTime time NOT NULL, EndTime time NOT NULL, StartsAt datetime2(3) NOT NULL, EndsAt datetime2(3) NOT NULL,
                MinimumStaff int NOT NULL,
                IsCancelled bit NOT NULL,
                CONSTRAINT FK_DutyRosterShifts_Roster FOREIGN KEY (DutyRosterId) REFERENCES dbo.DutyRosters(Id),
                CONSTRAINT FK_DutyRosterShifts_Template FOREIGN KEY (WorkShiftTemplateId) REFERENCES dbo.WorkShiftTemplates(Id),
                CONSTRAINT CK_DutyRosterShifts_Hours CHECK (StartsAt < EndsAt),
                CONSTRAINT CK_DutyRosterShifts_MinimumStaff CHECK (MinimumStaff >= 0)
            );
            CREATE INDEX IX_DutyRosterShifts_DutyRosterId_WorkDate ON dbo.DutyRosterShifts(DutyRosterId, WorkDate);
        END;
        IF OBJECT_ID('dbo.DutyAssignments', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.DutyAssignments (
                Id int IDENTITY PRIMARY KEY, DutyRosterShiftId int NOT NULL, EmployeeId int NOT NULL,
                Notes nvarchar(1000) NULL, IsCancelled bit NOT NULL,
                CONSTRAINT FK_DutyAssignments_Shift FOREIGN KEY (DutyRosterShiftId) REFERENCES dbo.DutyRosterShifts(Id),
                CONSTRAINT FK_DutyAssignments_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Employees(Id)
            );
            CREATE INDEX IX_DutyAssignments_EmployeeId_IsCancelled ON dbo.DutyAssignments(EmployeeId, IsCancelled);
        END;
        IF COL_LENGTH('dbo.DutyAssignments', 'Notes') IS NULL
            ALTER TABLE dbo.DutyAssignments ADD Notes nvarchar(1000) NULL;
        IF COL_LENGTH('dbo.DutyAssignments', 'AttendanceTrackingFrom') IS NULL
            ALTER TABLE dbo.DutyAssignments ADD AttendanceTrackingFrom datetime2(3) NOT NULL
                CONSTRAINT DF_DutyAssignments_AttendanceTrackingFrom DEFAULT(sysutcdatetime()) WITH VALUES;
        IF OBJECT_ID('dbo.DutyAttendances', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.DutyAttendances (
                Id int IDENTITY PRIMARY KEY, DutyAssignmentId int NOT NULL,
                CheckInAt datetime2(3) NULL, CheckOutAt datetime2(3) NULL,
                IsAbsent bit NOT NULL, IsLate bit NOT NULL, AutoEnded bit NOT NULL, Revision int NOT NULL,
                CONSTRAINT FK_DutyAttendances_Assignment FOREIGN KEY (DutyAssignmentId) REFERENCES dbo.DutyAssignments(Id),
                CONSTRAINT CK_DutyAttendances_Times CHECK (CheckOutAt IS NULL OR (CheckInAt IS NOT NULL AND CheckOutAt >= CheckInAt))
            );
            CREATE UNIQUE INDEX IX_DutyAttendances_DutyAssignmentId ON dbo.DutyAttendances(DutyAssignmentId);
        END;
        IF OBJECT_ID('dbo.DutyAttendanceEvents', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.DutyAttendanceEvents (
                Id bigint IDENTITY PRIMARY KEY, DutyAssignmentId int NOT NULL, Revision int NOT NULL,
                Action varchar(30) NOT NULL, ActorId nvarchar(450) NOT NULL, Reason nvarchar(500) NULL,
                CreatedAt datetime2(3) NOT NULL, BeforeJson nvarchar(max) NULL, AfterJson nvarchar(max) NULL,
                CONSTRAINT FK_DutyAttendanceEvents_Assignment FOREIGN KEY (DutyAssignmentId) REFERENCES dbo.DutyAssignments(Id)
            );
            CREATE UNIQUE INDEX IX_DutyAttendanceEvents_DutyAssignmentId_Revision ON dbo.DutyAttendanceEvents(DutyAssignmentId,Revision);
        END;
        IF OBJECT_ID('dbo.DutyRosterChangeLogs', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.DutyRosterChangeLogs (
                Id bigint IDENTITY PRIMARY KEY, DutyRosterId int NOT NULL, Revision int NOT NULL,
                Action varchar(40) NOT NULL, ChangedBy nvarchar(450) NOT NULL, ChangedAt datetime2(3) NOT NULL,
                Reason nvarchar(500) NULL, BeforeJson nvarchar(max) NULL, AfterJson nvarchar(max) NULL,
                CONSTRAINT FK_DutyRosterChangeLogs_Roster FOREIGN KEY (DutyRosterId) REFERENCES dbo.DutyRosters(Id)
            );
            CREATE UNIQUE INDEX IX_DutyRosterChangeLogs_DutyRosterId_Revision ON dbo.DutyRosterChangeLogs(DutyRosterId, Revision);
        END;
        """ + SqlWorkRotation + SqlWorkRequests + SqlAttendancePeriods + SqlPayroll + SqlMinimumStaff;
}
