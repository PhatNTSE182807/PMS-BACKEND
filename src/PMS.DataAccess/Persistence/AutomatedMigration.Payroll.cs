namespace PMS.DataAccess.Persistence;
public static partial class AutomatedMigration
{
 public const string SqlPayroll = """
IF OBJECT_ID('dbo.PayrollProfiles', 'U') IS NULL
BEGIN
 CREATE TABLE dbo.PayrollProfiles (Id int IDENTITY PRIMARY KEY, UserId nvarchar(450) NOT NULL, Role nvarchar(20) NOT NULL, EffectiveFrom date NOT NULL, HourlyRate decimal(18,6) NULL, MonthlyRate decimal(18,6) NULL, NightStart time NULL, NightEnd time NULL, NightFactor decimal(18,6) NULL, HolidayFactor decimal(18,6) NULL, OvertimeFactor decimal(18,6) NULL, OvertimeCombination nvarchar(40) NULL, StandbyDailyAllowance decimal(18,6) NULL, PaidLeaveMethod nvarchar(30) NULL, PaidLeaveHoursPerDay decimal(18,6) NULL, StandardWorkDays decimal(18,6) NULL, ManagerProration nvarchar(30) NULL, ManagerUnpaidLeavePolicy nvarchar(30) NULL, Currency nvarchar(3) NULL, MoneyDecimals int NULL, RoundingMode nvarchar(30) NULL, Reason nvarchar(1000) NOT NULL, Revision int NOT NULL, CreatedBy nvarchar(450) NOT NULL, CreatedAt datetime2 NOT NULL);
END;
IF OBJECT_ID('dbo.PayrollAttendanceSources', 'U') IS NULL
BEGIN
 CREATE TABLE dbo.PayrollAttendanceSources (Id int IDENTITY PRIMARY KEY, AttendancePeriodLotId int NOT NULL REFERENCES dbo.AttendancePeriodLots(Id), SnapshotJson nvarchar(max) NOT NULL, SnapshotHash nvarchar(64) NOT NULL, CreatedBy nvarchar(450) NOT NULL, CreatedAt datetime2 NOT NULL);
END;
IF OBJECT_ID('dbo.PayrollSlips', 'U') IS NULL
BEGIN
 CREATE TABLE dbo.PayrollSlips (Id int IDENTITY PRIMARY KEY, UserId nvarchar(450) NOT NULL, EmployeeId int NULL, EmployeeName nvarchar(200) NOT NULL, Role nvarchar(20) NOT NULL, ParkingLotId int NOT NULL REFERENCES dbo.ParkingLots(Id), PayMonth date NOT NULL, Status nvarchar(20) NOT NULL, Currency nvarchar(3) NOT NULL, Total decimal(18,4) NOT NULL, BasisHash nvarchar(64) NOT NULL, BasisJson nvarchar(max) NOT NULL, SourceIdsJson nvarchar(max) NOT NULL, Revision int NOT NULL, CreatedAt datetime2 NOT NULL, ApprovedAt datetime2 NULL, PaidAt datetime2 NULL, TransactionReference nvarchar(200) NULL);
END;
IF OBJECT_ID('dbo.PayrollLines', 'U') IS NULL
BEGIN
 CREATE TABLE dbo.PayrollLines (Id int IDENTITY PRIMARY KEY, PayrollSlipId int NOT NULL REFERENCES dbo.PayrollSlips(Id), Ordinal int NOT NULL, Kind nvarchar(40) NOT NULL, Description nvarchar(500) NOT NULL, SourcePeriodLotId int NOT NULL, AssignmentId int NULL REFERENCES dbo.DutyAssignments(Id), AttendanceRevision int NULL, StandbyId int NULL, LeaveRequestId int NULL, ManagerDayId int NULL, AdjustmentId int NULL, ProfileId int NULL REFERENCES dbo.PayrollProfiles(Id), WorkDate date NOT NULL, StartsAt datetime2 NULL, EndsAt datetime2 NULL, Unit nvarchar(20) NOT NULL, Quantity decimal(28,16) NOT NULL, UnitRate decimal(18,6) NOT NULL, Factor decimal(18,6) NOT NULL, Amount decimal(18,4) NOT NULL, SourceJson nvarchar(max) NOT NULL);
END;
IF OBJECT_ID('dbo.PayrollAdjustments', 'U') IS NULL
BEGIN
 CREATE TABLE dbo.PayrollAdjustments (Id int IDENTITY PRIMARY KEY, UserId nvarchar(450) NOT NULL, Role nvarchar(20) NOT NULL, ParkingLotId int NOT NULL REFERENCES dbo.ParkingLots(Id), PayMonth date NOT NULL, SourceSlipId int NULL REFERENCES dbo.PayrollSlips(Id), SourceComplaintId int NULL, SourceCarryId int NULL, Kind nvarchar(30) NOT NULL, Quantity decimal(28,16) NOT NULL, UnitRate decimal(18,6) NOT NULL, Reason nvarchar(1000) NOT NULL, CreatedBy nvarchar(450) NOT NULL, CreatedAt datetime2 NOT NULL);
END;
IF OBJECT_ID('dbo.PayrollComplaints', 'U') IS NULL
BEGIN
 CREATE TABLE dbo.PayrollComplaints (Id int IDENTITY PRIMARY KEY, PayrollSlipId int NOT NULL REFERENCES dbo.PayrollSlips(Id), UserId nvarchar(450) NOT NULL, Reason nvarchar(1000) NOT NULL, Status nvarchar(20) NOT NULL, ResolutionReason nvarchar(1000) NULL, AdjustmentId int NULL, Revision int NOT NULL, CreatedAt datetime2 NOT NULL, ResolvedAt datetime2 NULL);
END;
IF OBJECT_ID('dbo.PayrollAudits', 'U') IS NULL
BEGIN
 CREATE TABLE dbo.PayrollAudits (Id bigint IDENTITY PRIMARY KEY, ActorId nvarchar(450) NOT NULL, Action nvarchar(50) NOT NULL, ParkingLotId int NULL, PayrollSlipId int NULL, Reason nvarchar(1000) NULL, BeforeJson nvarchar(max) NULL, AfterJson nvarchar(max) NULL, CreatedAt datetime2 NOT NULL);
END;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='UX_PayrollProfile_UserDate' AND object_id=OBJECT_ID('dbo.PayrollProfiles')) CREATE UNIQUE INDEX UX_PayrollProfile_UserDate ON dbo.PayrollProfiles(UserId,EffectiveFrom);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='UX_PayrollSource_Lot' AND object_id=OBJECT_ID('dbo.PayrollAttendanceSources')) CREATE UNIQUE INDEX UX_PayrollSource_Lot ON dbo.PayrollAttendanceSources(AttendancePeriodLotId);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='UX_PayrollSlip_UserLotMonth' AND object_id=OBJECT_ID('dbo.PayrollSlips')) CREATE UNIQUE INDEX UX_PayrollSlip_UserLotMonth ON dbo.PayrollSlips(UserId,ParkingLotId,PayMonth);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='UX_PayrollSlip_ManagerMonth' AND object_id=OBJECT_ID('dbo.PayrollSlips')) CREATE UNIQUE INDEX UX_PayrollSlip_ManagerMonth ON dbo.PayrollSlips(UserId,PayMonth,Role) WHERE Role=N'Manager';
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='UX_PayrollLine_Order' AND object_id=OBJECT_ID('dbo.PayrollLines')) CREATE UNIQUE INDEX UX_PayrollLine_Order ON dbo.PayrollLines(PayrollSlipId,Ordinal);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='UX_PayrollAdjustment_Complaint' AND object_id=OBJECT_ID('dbo.PayrollAdjustments')) CREATE UNIQUE INDEX UX_PayrollAdjustment_Complaint ON dbo.PayrollAdjustments(SourceComplaintId) WHERE SourceComplaintId IS NOT NULL;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='UX_PayrollAdjustment_Carry' AND object_id=OBJECT_ID('dbo.PayrollAdjustments')) CREATE UNIQUE INDEX UX_PayrollAdjustment_Carry ON dbo.PayrollAdjustments(SourceCarryId) WHERE SourceCarryId IS NOT NULL;
""";
}
