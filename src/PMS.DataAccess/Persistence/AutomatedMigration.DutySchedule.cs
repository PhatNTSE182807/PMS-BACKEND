using Microsoft.EntityFrameworkCore;

namespace PMS.DataAccess.Persistence;

public static partial class AutomatedMigration
{
    // Additive and repeatable. The tables of the old rosters / rotation / attendance periods / payroll
    // are left in place and no longer used; WorkLeaveBalances is shared with them (same columns).
    private static Task EnsureDutyScheduleTablesAsync(PmsDbContext db) => db.Database.ExecuteSqlRawAsync("""
        IF COL_LENGTH('dbo.Employees', 'EmploymentType') IS NULL
            ALTER TABLE dbo.Employees ADD EmploymentType varchar(20) NOT NULL
                CONSTRAINT DF_Employees_EmploymentType DEFAULT 'FullTime';
        IF OBJECT_ID('dbo.DutyPatterns','U') IS NULL
        BEGIN
            CREATE TABLE dbo.DutyPatterns (Id int IDENTITY PRIMARY KEY, ParkingLotId int NOT NULL,
                EffectiveFrom date NOT NULL, CreatedByUserId nvarchar(450) NULL, CreatedAt datetime2(3) NOT NULL,
                CONSTRAINT FK_DutyPatterns_Lot FOREIGN KEY (ParkingLotId) REFERENCES dbo.ParkingLots(Id));
            CREATE UNIQUE INDEX IX_DutyPatterns_Lot_From ON dbo.DutyPatterns(ParkingLotId, EffectiveFrom);
        END;
        IF OBJECT_ID('dbo.DutyPatternCells','U') IS NULL
        BEGIN
            CREATE TABLE dbo.DutyPatternCells (Id int IDENTITY PRIMARY KEY, DutyPatternId int NOT NULL,
                DayOfWeek int NOT NULL, Slot int NOT NULL, EmployeeId int NOT NULL,
                CONSTRAINT FK_DutyPatternCells_Pattern FOREIGN KEY (DutyPatternId) REFERENCES dbo.DutyPatterns(Id) ON DELETE CASCADE,
                CONSTRAINT FK_DutyPatternCells_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Employees(Id),
                CONSTRAINT CK_DutyPatternCells_Cell CHECK (DayOfWeek BETWEEN 1 AND 7 AND Slot BETWEEN 0 AND 3));
            CREATE UNIQUE INDEX IX_DutyPatternCells_Cell ON dbo.DutyPatternCells(DutyPatternId, DayOfWeek, Slot);
        END;
        IF OBJECT_ID('dbo.DutyOverrides','U') IS NULL
        BEGIN
            CREATE TABLE dbo.DutyOverrides (Id int IDENTITY PRIMARY KEY, ParkingLotId int NOT NULL,
                WorkDate date NOT NULL, Slot int NOT NULL, EmployeeId int NULL, LeaveRequestId int NULL,
                Reason nvarchar(500) NULL, CreatedByUserId nvarchar(450) NULL, CreatedAt datetime2(3) NOT NULL,
                CONSTRAINT FK_DutyOverrides_Lot FOREIGN KEY (ParkingLotId) REFERENCES dbo.ParkingLots(Id),
                CONSTRAINT FK_DutyOverrides_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Employees(Id),
                CONSTRAINT CK_DutyOverrides_Slot CHECK (Slot BETWEEN 0 AND 3));
            CREATE UNIQUE INDEX IX_DutyOverrides_Cell ON dbo.DutyOverrides(ParkingLotId, WorkDate, Slot);
        END;
        IF OBJECT_ID('dbo.DutyShiftAttendances','U') IS NULL
        BEGIN
            CREATE TABLE dbo.DutyShiftAttendances (Id bigint IDENTITY PRIMARY KEY, ParkingLotId int NOT NULL,
                EmployeeId int NOT NULL, WorkDate date NOT NULL, Slot int NOT NULL, Status varchar(20) NOT NULL,
                CheckInAt datetime2(3) NULL, CheckOutAt datetime2(3) NULL, IsAutoEnded bit NOT NULL,
                AdjustedByUserId nvarchar(450) NULL, AdjustReason nvarchar(500) NULL,
                CreatedAt datetime2(3) NOT NULL, UpdatedAt datetime2(3) NOT NULL,
                CONSTRAINT FK_DutyShiftAttendances_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Employees(Id));
            CREATE UNIQUE INDEX IX_DutyShiftAttendances_Shift ON dbo.DutyShiftAttendances(ParkingLotId, WorkDate, Slot, EmployeeId);
            CREATE INDEX IX_DutyShiftAttendances_Employee ON dbo.DutyShiftAttendances(EmployeeId, WorkDate);
        END;
        IF OBJECT_ID('dbo.DutyLeaveRequests','U') IS NULL
        BEGIN
            CREATE TABLE dbo.DutyLeaveRequests (Id int IDENTITY PRIMARY KEY, EmployeeId int NOT NULL, ParkingLotId int NOT NULL,
                FromDate date NOT NULL, ToDate date NOT NULL, Reason nvarchar(500) NOT NULL, Status varchar(20) NOT NULL,
                DecisionReason nvarchar(500) NULL, DecidedByUserId nvarchar(450) NULL, DecidedAt datetime2(3) NULL,
                CreatedAt datetime2(3) NOT NULL,
                CONSTRAINT FK_DutyLeaveRequests_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Employees(Id),
                CONSTRAINT CK_DutyLeaveRequests_Range CHECK (FromDate <= ToDate));
            CREATE INDEX IX_DutyLeaveRequests_Lot_Status ON dbo.DutyLeaveRequests(ParkingLotId, Status);
            CREATE INDEX IX_DutyLeaveRequests_Employee ON dbo.DutyLeaveRequests(EmployeeId, FromDate);
        END;
        IF OBJECT_ID('dbo.DutyLeaveDays','U') IS NULL
        BEGIN
            CREATE TABLE dbo.DutyLeaveDays (Id int IDENTITY PRIMARY KEY, DutyLeaveRequestId int NOT NULL, EmployeeId int NOT NULL,
                LeaveDate date NOT NULL, HadShift bit NOT NULL, IsPaid bit NOT NULL,
                CONSTRAINT FK_DutyLeaveDays_Request FOREIGN KEY (DutyLeaveRequestId) REFERENCES dbo.DutyLeaveRequests(Id));
            CREATE INDEX IX_DutyLeaveDays_Employee ON dbo.DutyLeaveDays(EmployeeId, LeaveDate);
        END;
        IF OBJECT_ID('dbo.WorkLeaveBalances','U') IS NULL
        BEGIN
            CREATE TABLE dbo.WorkLeaveBalances (Id int IDENTITY PRIMARY KEY, UserId nvarchar(450) NOT NULL,
                Year int NOT NULL, EntitledDays int NOT NULL, Revision int NOT NULL);
            CREATE UNIQUE INDEX IX_WorkLeaveBalances_UserId_Year ON dbo.WorkLeaveBalances(UserId, Year);
        END;
        IF OBJECT_ID('dbo.PaySlips','U') IS NULL
        BEGIN
            CREATE TABLE dbo.PaySlips (Id bigint IDENTITY PRIMARY KEY, EmployeeId int NOT NULL, UserId nvarchar(450) NOT NULL,
                ParkingLotId int NULL, Month date NOT NULL, Kind varchar(20) NOT NULL,
                BaseAmount decimal(18,0) NOT NULL, Deductions decimal(18,0) NOT NULL, Overtime decimal(18,0) NOT NULL,
                Adjustments decimal(18,0) NOT NULL, NetAmount decimal(18,0) NOT NULL, Status varchar(20) NOT NULL,
                IsSimulated bit NOT NULL, PaidAt datetime2(3) NULL, PaidByUserId nvarchar(450) NULL, LinesJson nvarchar(max) NULL,
                CreatedAt datetime2(3) NOT NULL, UpdatedAt datetime2(3) NOT NULL,
                CONSTRAINT FK_PaySlips_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Employees(Id));
            CREATE INDEX IX_PaySlips_Month_Status ON dbo.PaySlips(Month, Status);
            CREATE INDEX IX_PaySlips_Employee_Month ON dbo.PaySlips(EmployeeId, Month);
            -- One real payslip per person and month; simulated ones are extra.
            CREATE UNIQUE INDEX IX_PaySlips_Real ON dbo.PaySlips(EmployeeId, Month) WHERE IsSimulated = 0;
        END;
        IF OBJECT_ID('dbo.PayAdjustments','U') IS NULL
        BEGIN
            CREATE TABLE dbo.PayAdjustments (Id int IDENTITY PRIMARY KEY, EmployeeId int NOT NULL, Month date NOT NULL,
                Amount decimal(18,0) NOT NULL, Reason nvarchar(500) NOT NULL, CreatedByUserId nvarchar(450) NULL,
                CreatedAt datetime2(3) NOT NULL,
                CONSTRAINT FK_PayAdjustments_Employee FOREIGN KEY (EmployeeId) REFERENCES dbo.Employees(Id));
            CREATE INDEX IX_PayAdjustments_Employee_Month ON dbo.PayAdjustments(EmployeeId, Month);
        END;
        """);
}
