namespace PMS.Application.Models.WorkScheduling;

public record RotationMemberInput(int EmployeeId);
public class RotationGroupInput
{
    public string Name { get; set; }
    public int OffsetDays { get; set; }
    public bool IsActive { get; set; } = true;
    public int Revision { get; set; }
    public List<RotationMemberInput> Members { get; set; } = [];
}
public record RotationGroupModel(int Id, string Name, int OffsetDays, bool IsActive, int Revision, IReadOnlyList<RotationMemberInput> Members);
// A null template is a rest block, not approved leave and not standby.
public record RotationStep(int? TemplateId, int Days);
public class RotationCycleInput
{
    public string Name { get; set; }
    public int Revision { get; set; }
    public List<RotationStep> Steps { get; set; } = [];
}
public record RotationCycleModel(int Id, string Name, int Revision, IReadOnlyList<RotationStep> Steps);
public class RotationGenerationInput : RosterMutationInput
{
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    public DateOnly CycleStartDate { get; set; }
    public int CycleId { get; set; }
    public List<int> GroupIds { get; set; } = [];
    public bool ReplaceGenerated { get; set; }
    public bool IncludeStandby { get; set; }
    public List<int> StandbyGroupIds { get; set; } = [];
    public string PreviewToken { get; set; }
}
public record RotationIssue(string Code, DateOnly? Date, int? EmployeeId, string Message, bool Blocking = true);
public record RotationChange(string Kind, DateOnly Date, int EmployeeId, string StaffName, string Detail, int? AssignmentId = null);
public record StandbyModel(int Id, DateOnly WorkDate, int EmployeeId, string StaffName, string Source);
public class StandbyMutationInput : RosterMutationInput
{
    public DateOnly WorkDate { get; set; }
    public int? EmployeeId { get; set; }
}
public record RotationPreviewModel(DutyRosterModel Roster, string Token, int ProposedAssignments,
    IReadOnlyList<RotationChange> Changes, IReadOnlyList<RotationIssue> Issues, IReadOnlyList<RosterStaffModel> Unassigned,
    decimal? MinimumRestHours, decimal? MaximumWeeklyHours, bool LeaveSourceAvailable);
public class WorkRotationRules
{
    // Confirmed by the user: 60 minutes between shifts, 48 hours per week.
    public decimal? MinimumRestHours { get; set; } = 1;
    public decimal? MaximumWeeklyHours { get; set; } = 48;
    public void Validate()
    {
        if (MinimumRestHours < 0 || MaximumWeeklyHours <= 0 || MaximumWeeklyHours > 168)
            throw new InvalidOperationException("Invalid WorkRotation timing configuration.");
    }
}
