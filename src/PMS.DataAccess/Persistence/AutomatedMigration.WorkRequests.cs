namespace PMS.DataAccess.Persistence;
public static partial class AutomatedMigration
{
    public const string SqlWorkRequests = """
        IF COL_LENGTH('dbo.DutyAssignments','OriginalAssignmentId') IS NULL
            ALTER TABLE dbo.DutyAssignments ADD OriginalAssignmentId int NULL, ReplacementReason nvarchar(1000) NULL,
                AllowWeeklyOvertime bit NOT NULL CONSTRAINT DF_DutyAssignments_Overtime DEFAULT(0) WITH VALUES;
        IF COL_LENGTH('dbo.DutyAssignments','CancellationKind') IS NULL
            ALTER TABLE dbo.DutyAssignments ADD CancellationKind nvarchar(30) NULL;
        IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name='FK_DutyAssignments_Original')
            ALTER TABLE dbo.DutyAssignments ADD CONSTRAINT FK_DutyAssignments_Original FOREIGN KEY(OriginalAssignmentId) REFERENCES dbo.DutyAssignments(Id);
        IF OBJECT_ID('dbo.WorkLeaveRequests','U') IS NULL
        BEGIN
            CREATE TABLE dbo.WorkLeaveRequests (
                Id int IDENTITY PRIMARY KEY, ParkingLotId int NOT NULL, DutyAssignmentId int NOT NULL, EmployeeId int NOT NULL,
                ProposedEmployeeId int NULL, Kind nvarchar(20) NOT NULL, Reason nvarchar(1000) NOT NULL,
                Status nvarchar(30) NOT NULL, IsPaid bit NOT NULL, LeaveDate date NOT NULL,
                StartsAt datetime2 NOT NULL, EndsAt datetime2 NOT NULL, Revision int NOT NULL, CreatedAt datetime2 NOT NULL,
                DecidedAt datetime2 NULL, DecisionReason nvarchar(1000) NULL,
                FOREIGN KEY(DutyAssignmentId) REFERENCES dbo.DutyAssignments(Id), FOREIGN KEY(EmployeeId) REFERENCES dbo.Employees(Id));
            CREATE INDEX IX_WorkLeaveRequests_ParkingLotId_Status ON dbo.WorkLeaveRequests(ParkingLotId,Status);
        END;
        IF OBJECT_ID('dbo.WorkLeaveBalances','U') IS NULL
        BEGIN
            CREATE TABLE dbo.WorkLeaveBalances (Id int IDENTITY PRIMARY KEY, UserId nvarchar(450) NOT NULL,
                Year int NOT NULL, EntitledDays int NOT NULL, Revision int NOT NULL);
            CREATE UNIQUE INDEX IX_WorkLeaveBalances_UserId_Year ON dbo.WorkLeaveBalances(UserId,Year);
        END;
        IF OBJECT_ID('dbo.WorkLeaveDays','U') IS NULL
        BEGIN
            CREATE TABLE dbo.WorkLeaveDays (Id int IDENTITY PRIMARY KEY, UserId nvarchar(450) NOT NULL,
                LeaveDate date NOT NULL, IsPaid bit NOT NULL, FirstLeaveRequestId int NOT NULL,
                FOREIGN KEY(FirstLeaveRequestId) REFERENCES dbo.WorkLeaveRequests(Id));
            CREATE UNIQUE INDEX IX_WorkLeaveDays_UserId_LeaveDate ON dbo.WorkLeaveDays(UserId,LeaveDate);
        END;
        IF OBJECT_ID('dbo.WorkSwapRequests','U') IS NULL
            CREATE TABLE dbo.WorkSwapRequests (Id int IDENTITY PRIMARY KEY, ParkingLotId int NOT NULL,
                AssignmentId int NOT NULL, OtherAssignmentId int NOT NULL, EmployeeId int NOT NULL, OtherEmployeeId int NOT NULL,
                Reason nvarchar(1000) NOT NULL, Status nvarchar(30) NOT NULL, Revision int NOT NULL,
                CreatedAt datetime2 NOT NULL, DecisionReason nvarchar(1000) NULL,
                FOREIGN KEY(AssignmentId) REFERENCES dbo.DutyAssignments(Id), FOREIGN KEY(OtherAssignmentId) REFERENCES dbo.DutyAssignments(Id));
        IF OBJECT_ID('dbo.WorkOpenShifts','U') IS NULL
        BEGIN
            CREATE TABLE dbo.WorkOpenShifts (Id int IDENTITY PRIMARY KEY, ParkingLotId int NOT NULL,
                DutyRosterShiftId int NOT NULL, OriginalAssignmentId int NULL, LeaveRequestId int NULL, ProposedEmployeeId int NULL, TargetEmployeeId int NULL,
                Reason nvarchar(1000) NOT NULL, Status nvarchar(30) NOT NULL, Stage nvarchar(30) NOT NULL,
                DeadlineAt datetime2 NULL, Revision int NOT NULL, ReplacementAssignmentId int NULL, CreatedAt datetime2 NOT NULL,
                FOREIGN KEY(DutyRosterShiftId) REFERENCES dbo.DutyRosterShifts(Id), FOREIGN KEY(OriginalAssignmentId) REFERENCES dbo.DutyAssignments(Id));
            CREATE INDEX IX_WorkOpenShifts_ParkingLotId_Status ON dbo.WorkOpenShifts(ParkingLotId,Status);
        END;
        IF OBJECT_ID('dbo.WorkOpenShiftClaims','U') IS NULL
        BEGIN
            CREATE TABLE dbo.WorkOpenShiftClaims (Id int IDENTITY PRIMARY KEY, WorkOpenShiftId int NOT NULL,
                EmployeeId int NOT NULL, Status nvarchar(30) NOT NULL, CreatedAt datetime2 NOT NULL,
                FOREIGN KEY(WorkOpenShiftId) REFERENCES dbo.WorkOpenShifts(Id), FOREIGN KEY(EmployeeId) REFERENCES dbo.Employees(Id));
            CREATE UNIQUE INDEX IX_WorkOpenShiftClaims_WorkOpenShiftId_EmployeeId ON dbo.WorkOpenShiftClaims(WorkOpenShiftId,EmployeeId);
        END;
        IF OBJECT_ID('dbo.WorkManagerCoverages','U') IS NULL
        BEGIN
            CREATE TABLE dbo.WorkManagerCoverages (Id int IDENTITY PRIMARY KEY, WorkOpenShiftId int NOT NULL, DutyRosterShiftId int NOT NULL,
                ManagerUserId nvarchar(450) NOT NULL, Reason nvarchar(1000) NOT NULL, CreatedAt datetime2 NOT NULL,
                FOREIGN KEY(WorkOpenShiftId) REFERENCES dbo.WorkOpenShifts(Id), FOREIGN KEY(DutyRosterShiftId) REFERENCES dbo.DutyRosterShifts(Id));
            CREATE UNIQUE INDEX IX_WorkManagerCoverages_WorkOpenShiftId ON dbo.WorkManagerCoverages(WorkOpenShiftId);
        END;
        IF OBJECT_ID('dbo.WorkScheduleWorkflowEvents','U') IS NULL
        BEGIN
            CREATE TABLE dbo.WorkScheduleWorkflowEvents (Id bigint IDENTITY PRIMARY KEY, ParkingLotId int NOT NULL,
                Kind nvarchar(30) NOT NULL, EntityId int NOT NULL, Action nvarchar(50) NOT NULL, ActorId nvarchar(450) NOT NULL,
                Reason nvarchar(1000) NULL, BeforeJson nvarchar(max) NULL, AfterJson nvarchar(max) NULL, CreatedAt datetime2 NOT NULL);
            CREATE INDEX IX_WorkScheduleWorkflowEvents_ParkingLotId_Kind_EntityId ON dbo.WorkScheduleWorkflowEvents(ParkingLotId,Kind,EntityId);
        END;
        """;
}
