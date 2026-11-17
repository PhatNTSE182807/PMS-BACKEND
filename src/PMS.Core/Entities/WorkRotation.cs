namespace PMS.DataAccess.Persistence.Entities;

public class WorkRotationGroup
{
    public int Id { get; set; }
    public int ParkingLotId { get; set; }
    public string Name { get; set; }
    public int OffsetDays { get; set; }
    public bool IsActive { get; set; } = true;
    public int Revision { get; set; } = 1;
}
public class WorkRotationMember
{
    public int WorkRotationGroupId { get; set; }
    public int EmployeeId { get; set; }
}
public class WorkRotationCycle
{
    public int Id { get; set; }
    public int ParkingLotId { get; set; }
    public string Name { get; set; }
    public string StepsJson { get; set; }
    public int Revision { get; set; } = 1;
}
public class DutyStandbyAssignment
{
    public bool WasCalled { get; set; }
    public int Id { get; set; }
    public int DutyRosterId { get; set; }
    public DateOnly WorkDate { get; set; }
    public int EmployeeId { get; set; }
    public string Source { get; set; } = "Rotation";
    public bool IsCancelled { get; set; }
}
