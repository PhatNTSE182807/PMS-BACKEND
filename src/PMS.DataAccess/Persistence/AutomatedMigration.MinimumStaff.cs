namespace PMS.DataAccess.Persistence;

public static partial class AutomatedMigration
{
    // Shifts need a single minimum head count instead of separate Entry, Exit and Counter minimums.
    // Guarded and repeatable: the new column is filled from the old three before they are dropped,
    // so a run that stops half way is completed by the next one. Every statement that names a column
    // that may not exist yet (or any more) runs through EXEC, because SQL Server compiles the whole batch.
    // The old per-shift Position columns are kept for existing rows but become nullable and unused.
    public const string SqlMinimumStaff = """

        IF COL_LENGTH('dbo.WorkShiftTemplates', 'MinimumStaff') IS NULL
            ALTER TABLE dbo.WorkShiftTemplates ADD MinimumStaff int NOT NULL
                CONSTRAINT DF_WorkShiftTemplates_MinimumStaff DEFAULT (0) WITH VALUES;
        IF COL_LENGTH('dbo.WorkShiftTemplates', 'MinimumEntryStaff') IS NOT NULL
        BEGIN
            EXEC(N'UPDATE dbo.WorkShiftTemplates
                SET MinimumStaff = ISNULL(MinimumEntryStaff, 0) + ISNULL(MinimumExitStaff, 0) + ISNULL(MinimumCounterStaff, 0);');
            IF OBJECT_ID('dbo.CK_WorkShiftTemplates_Minimum', 'C') IS NOT NULL
                ALTER TABLE dbo.WorkShiftTemplates DROP CONSTRAINT CK_WorkShiftTemplates_Minimum;
            EXEC(N'ALTER TABLE dbo.WorkShiftTemplates DROP COLUMN MinimumEntryStaff, MinimumExitStaff, MinimumCounterStaff;');
        END;
        IF OBJECT_ID('dbo.CK_WorkShiftTemplates_MinimumStaff', 'C') IS NULL
            EXEC(N'ALTER TABLE dbo.WorkShiftTemplates ADD CONSTRAINT CK_WorkShiftTemplates_MinimumStaff CHECK (MinimumStaff >= 0);');

        IF COL_LENGTH('dbo.DutyRosterShifts', 'MinimumStaff') IS NULL
            ALTER TABLE dbo.DutyRosterShifts ADD MinimumStaff int NOT NULL
                CONSTRAINT DF_DutyRosterShifts_MinimumStaff DEFAULT (0) WITH VALUES;
        IF COL_LENGTH('dbo.DutyRosterShifts', 'MinimumEntryStaff') IS NOT NULL
        BEGIN
            EXEC(N'UPDATE dbo.DutyRosterShifts
                SET MinimumStaff = ISNULL(MinimumEntryStaff, 0) + ISNULL(MinimumExitStaff, 0) + ISNULL(MinimumCounterStaff, 0);');
            IF OBJECT_ID('dbo.CK_DutyRosterShifts_Minimum', 'C') IS NOT NULL
                ALTER TABLE dbo.DutyRosterShifts DROP CONSTRAINT CK_DutyRosterShifts_Minimum;
            EXEC(N'ALTER TABLE dbo.DutyRosterShifts DROP COLUMN MinimumEntryStaff, MinimumExitStaff, MinimumCounterStaff;');
        END;
        IF OBJECT_ID('dbo.CK_DutyRosterShifts_MinimumStaff', 'C') IS NULL
            EXEC(N'ALTER TABLE dbo.DutyRosterShifts ADD CONSTRAINT CK_DutyRosterShifts_MinimumStaff CHECK (MinimumStaff >= 0);');

        IF OBJECT_ID('dbo.CK_DutyAssignments_Position', 'C') IS NOT NULL
            ALTER TABLE dbo.DutyAssignments DROP CONSTRAINT CK_DutyAssignments_Position;
        IF COLUMNPROPERTY(OBJECT_ID('dbo.DutyAssignments'), 'Position', 'AllowsNull') = 0
            EXEC(N'ALTER TABLE dbo.DutyAssignments ALTER COLUMN Position varchar(20) NULL;');
        IF COLUMNPROPERTY(OBJECT_ID('dbo.WorkRotationMembers'), 'Position', 'AllowsNull') = 0
            EXEC(N'ALTER TABLE dbo.WorkRotationMembers ALTER COLUMN Position varchar(20) NULL;');
        IF COLUMNPROPERTY(OBJECT_ID('dbo.WorkOpenShifts'), 'Position', 'AllowsNull') = 0
            EXEC(N'ALTER TABLE dbo.WorkOpenShifts ALTER COLUMN Position nvarchar(20) NULL;');
        IF COLUMNPROPERTY(OBJECT_ID('dbo.WorkManagerCoverages'), 'Position', 'AllowsNull') = 0
            EXEC(N'ALTER TABLE dbo.WorkManagerCoverages ALTER COLUMN Position nvarchar(20) NULL;');
        """;
}
