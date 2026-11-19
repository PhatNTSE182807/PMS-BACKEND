namespace PMS.DataAccess.Persistence;

public static partial class AutomatedMigration
{
    // Vacancies no longer move through Proposed, Standby, Public and Manual stages, and standby duty is gone.
    // Guarded and repeatable; statements that name a column that may be gone run through EXEC,
    // because SQL Server compiles the whole batch.
    //  - WorkOpenShifts.DeadlineAt now holds the single expiry: the shift start, or the shift end for a
    //    vacancy opened after the start. It is set once for existing vacancies, before Stage is retired.
    //  - Stage is kept for existing rows but becomes nullable and unused. The proposal columns
    //    (ProposedEmployeeId, TargetEmployeeId) were already nullable and are no longer used.
    //  - DutyStandbyAssignments is dropped. Payroll lines already paid for standby keep their rows;
    //    PayrollLines.StandbyId and PayrollProfiles.StandbyDailyAllowance stay as unused nullable columns.
    public const string SqlSimplifyVacancies = """

        IF COLUMNPROPERTY(OBJECT_ID('dbo.WorkOpenShifts'), 'Stage', 'AllowsNull') = 0
        BEGIN
            EXEC(N'UPDATE o SET DeadlineAt = CASE WHEN o.CreatedAt < s.StartsAt THEN s.StartsAt ELSE s.EndsAt END
                FROM dbo.WorkOpenShifts o JOIN dbo.DutyRosterShifts s ON s.Id = o.DutyRosterShiftId;');
            EXEC(N'ALTER TABLE dbo.WorkOpenShifts ALTER COLUMN Stage nvarchar(30) NULL;');
        END;

        IF OBJECT_ID('dbo.DutyStandbyAssignments', 'U') IS NOT NULL
        BEGIN
            DECLARE @dropStandbyKeys nvarchar(max) = N'';
            SELECT @dropStandbyKeys += N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N'.'
                + QUOTENAME(OBJECT_NAME(parent_object_id)) + N' DROP CONSTRAINT ' + QUOTENAME(name) + N';'
            FROM sys.foreign_keys WHERE referenced_object_id = OBJECT_ID('dbo.DutyStandbyAssignments');
            EXEC(@dropStandbyKeys);
            DROP TABLE dbo.DutyStandbyAssignments;
        END;
        """;
}
