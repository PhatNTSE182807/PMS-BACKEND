namespace PMS.DataAccess.Persistence;
public static partial class AutomatedMigration
{
    public const string SqlAttendancePeriods = """
IF OBJECT_ID('dbo.AttendancePeriods', 'U') IS NULL
CREATE TABLE dbo.AttendancePeriods (Id int IDENTITY PRIMARY KEY, Name nvarchar(100) NOT NULL, FromDate date NOT NULL, ToDate date NOT NULL, Status nvarchar(20) NOT NULL, Revision int NOT NULL, CreatedBy nvarchar(450) NOT NULL, CreatedAt datetime2 NOT NULL, LockedBy nvarchar(450) NULL, LockedAt datetime2 NULL);
IF OBJECT_ID('dbo.AttendancePeriodLots', 'U') IS NULL
CREATE TABLE dbo.AttendancePeriodLots (Id int IDENTITY PRIMARY KEY, AttendancePeriodId int NOT NULL REFERENCES dbo.AttendancePeriods(Id), ParkingLotId int NOT NULL REFERENCES dbo.ParkingLots(Id), TimeZoneId nvarchar(100) NOT NULL, StartsAt datetime2 NOT NULL, EndsAt datetime2 NOT NULL, Status nvarchar(20) NOT NULL, Revision int NOT NULL, ConfirmedBy nvarchar(450) NULL, ConfirmedAt datetime2 NULL, SnapshotJson nvarchar(max) NULL, CONSTRAINT UQ_AttendancePeriodLot UNIQUE(AttendancePeriodId,ParkingLotId));
IF OBJECT_ID('dbo.AttendanceOvertimeDecisions', 'U') IS NULL
CREATE TABLE dbo.AttendanceOvertimeDecisions (Id int IDENTITY PRIMARY KEY, DutyAssignmentId int NOT NULL REFERENCES dbo.DutyAssignments(Id), AttendanceRevision int NOT NULL, PlannedStart datetime2 NOT NULL, PlannedEnd datetime2 NOT NULL, Approved bit NOT NULL, Reason nvarchar(1000) NOT NULL, DecidedBy nvarchar(450) NOT NULL, CreatedAt datetime2 NOT NULL);
IF OBJECT_ID('dbo.ManagerWorkDays', 'U') IS NULL
CREATE TABLE dbo.ManagerWorkDays (Id int IDENTITY PRIMARY KEY, ParkingLotId int NOT NULL REFERENCES dbo.ParkingLots(Id), ManagerUserId nvarchar(450) NOT NULL, WorkDate date NOT NULL, Kind nvarchar(20) NOT NULL, Reason nvarchar(1000) NOT NULL, Revision int NOT NULL, CONSTRAINT UQ_ManagerWorkDay UNIQUE(ManagerUserId,WorkDate));
IF OBJECT_ID('dbo.AttendanceCarryAdjustments', 'U') IS NULL
CREATE TABLE dbo.AttendanceCarryAdjustments (Id int IDENTITY PRIMARY KEY, SourcePeriodLotId int NOT NULL REFERENCES dbo.AttendancePeriodLots(Id), TargetPeriodLotId int NOT NULL REFERENCES dbo.AttendancePeriodLots(Id), EmployeeId int NOT NULL REFERENCES dbo.Employees(Id), DutyAssignmentId int NULL REFERENCES dbo.DutyAssignments(Id), Hours decimal(12,4) NOT NULL, Reason nvarchar(1000) NOT NULL, CreatedBy nvarchar(450) NOT NULL, CreatedAt datetime2 NOT NULL);
IF OBJECT_ID('dbo.AttendancePeriodEvents', 'U') IS NULL
CREATE TABLE dbo.AttendancePeriodEvents (Id bigint IDENTITY PRIMARY KEY, AttendancePeriodLotId int NOT NULL REFERENCES dbo.AttendancePeriodLots(Id), Action nvarchar(40) NOT NULL, ActorId nvarchar(450) NOT NULL, Reason nvarchar(1000) NULL, BeforeJson nvarchar(max) NULL, AfterJson nvarchar(max) NULL, CreatedAt datetime2 NOT NULL);
IF COL_LENGTH('dbo.DutyAttendances','ReviewedRevision') IS NULL ALTER TABLE dbo.DutyAttendances ADD ReviewedRevision int NULL;
IF COL_LENGTH('dbo.AttendanceOvertimeDecisions','CalculationKey') IS NULL ALTER TABLE dbo.AttendanceOvertimeDecisions ADD CalculationKey nvarchar(64) NULL;
""";
}
