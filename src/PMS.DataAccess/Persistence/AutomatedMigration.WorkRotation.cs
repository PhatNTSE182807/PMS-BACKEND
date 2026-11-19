namespace PMS.DataAccess.Persistence;
public static partial class AutomatedMigration
{
    public const string SqlWorkRotation = """
        IF COL_LENGTH('dbo.DutyAssignments','Source') IS NULL
            ALTER TABLE dbo.DutyAssignments ADD Source nvarchar(20) NOT NULL CONSTRAINT DF_DutyAssignments_Source DEFAULT('Manual') WITH VALUES;
        IF COL_LENGTH('dbo.DutyAssignments','IsManuallyEdited') IS NULL
            ALTER TABLE dbo.DutyAssignments ADD IsManuallyEdited bit NOT NULL CONSTRAINT DF_DutyAssignments_Manual DEFAULT(0) WITH VALUES;
        IF COL_LENGTH('dbo.DutyAssignments','RotationGroupId') IS NULL
            ALTER TABLE dbo.DutyAssignments ADD RotationGroupId int NULL;
        IF COL_LENGTH('dbo.DutyAssignments','GenerationKey') IS NULL
            ALTER TABLE dbo.DutyAssignments ADD GenerationKey nvarchar(100) NULL;
        IF COL_LENGTH('dbo.DutyRosters','HasRotation') IS NULL
            ALTER TABLE dbo.DutyRosters ADD HasRotation bit NOT NULL CONSTRAINT DF_DutyRosters_Rotation DEFAULT(0) WITH VALUES;
        IF COL_LENGTH('dbo.DutyRosters','RotationConfigurationJson') IS NULL
            ALTER TABLE dbo.DutyRosters ADD RotationConfigurationJson nvarchar(max) NULL;
        IF OBJECT_ID('dbo.WorkRotationGroups','U') IS NULL
            CREATE TABLE dbo.WorkRotationGroups (
                Id int IDENTITY PRIMARY KEY, ParkingLotId int NOT NULL, Name nvarchar(100) NOT NULL,
                OffsetDays int NOT NULL, IsActive bit NOT NULL, Revision int NOT NULL,
                CONSTRAINT FK_WorkRotationGroups_Lot FOREIGN KEY(ParkingLotId) REFERENCES dbo.ParkingLots(Id));
        IF OBJECT_ID('dbo.WorkRotationMembers','U') IS NULL
            CREATE TABLE dbo.WorkRotationMembers (
                WorkRotationGroupId int NOT NULL, EmployeeId int NOT NULL,
                PRIMARY KEY(WorkRotationGroupId,EmployeeId),
                CONSTRAINT FK_WorkRotationMembers_Group FOREIGN KEY(WorkRotationGroupId) REFERENCES dbo.WorkRotationGroups(Id),
                CONSTRAINT FK_WorkRotationMembers_Employee FOREIGN KEY(EmployeeId) REFERENCES dbo.Employees(Id));
        IF OBJECT_ID('dbo.WorkRotationCycles','U') IS NULL
            CREATE TABLE dbo.WorkRotationCycles (
                Id int IDENTITY PRIMARY KEY, ParkingLotId int NOT NULL, Name nvarchar(100) NOT NULL,
                StepsJson nvarchar(max) NOT NULL, Revision int NOT NULL,
                CONSTRAINT FK_WorkRotationCycles_Lot FOREIGN KEY(ParkingLotId) REFERENCES dbo.ParkingLots(Id));
        """;
}
