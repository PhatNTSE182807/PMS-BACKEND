namespace PMS.Application.Models.WorkScheduling;

public class WorkAttendanceOptions
{
    public int EarlyStartMinutes { get; set; } = 30;
    public int LateAfterMinutes { get; set; } = 15;
    public int AbsentAfterMinutes { get; set; } = 30;
    public int EndGraceMinutes { get; set; } = 15;

    public void Validate()
    {
        if (EarlyStartMinutes < 0 || LateAfterMinutes < 0 || AbsentAfterMinutes <= LateAfterMinutes || EndGraceMinutes < 0
            || new[] { EarlyStartMinutes, LateAfterMinutes, AbsentAfterMinutes, EndGraceMinutes }.Any(n => n > 1440))
            throw new InvalidOperationException("Invalid WorkAttendance timing configuration.");
    }
}

public class AttendanceMutationInput
{
    public int Revision { get; set; }
    public int RosterRevision { get; set; }
    public string Reason { get; set; }
}

public class AttendanceAdjustmentInput : AttendanceMutationInput
{
    public DateTimeOffset? CheckInAt { get; set; }
    public DateTimeOffset? CheckOutAt { get; set; }
}

public record AttendanceModel(string Status, int Revision, DateTime? CheckInAt, DateTime? CheckOutAt,
    bool IsLate, bool AutoEnded, bool CanStart, bool CanEnd, bool CanReopen,
    DateTime WindowOpensAt, DateTime LateAfterAt, DateTime AbsentAfterAt, DateTime GraceEndsAt, DateTime ServerTime);

public record AttendanceEventModel(long Id, int Revision, string Action, string ActorName, string Reason,
    DateTime CreatedAt, string BeforeJson, string AfterJson);

public record AttendanceDetailModel(int AssignmentId, int EmployeeId, string StaffName, string ShiftName, string Position,
    int ParkingLotId, int RosterId, string RosterName, string RosterStatus, int RosterRevision,
    DateTime PlannedStart, DateTime PlannedEnd, string TimeZoneId, AttendanceModel Attendance,
    IReadOnlyList<AttendanceEventModel> History);
