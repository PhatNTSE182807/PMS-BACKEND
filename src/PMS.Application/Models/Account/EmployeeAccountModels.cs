namespace PMS.Application.Models.Account;

/// <summary>A Manager or Staff account with the lots it covers.</summary>
public class EmployeeAccountResponseModel
{
    public string Id { get; set; }

    public string UserName { get; set; }

    public string FullName { get; set; }

    public string Email { get; set; }

    public string PhoneNumber { get; set; }

    public string Role { get; set; }

    public string EmployeeCode { get; set; }

    public bool IsActive { get; set; }

    /// <summary>Employee record status: Active, Inactive or Terminated.</summary>
    public string Status { get; set; }

    public DateTime? CreatedAt { get; set; }

    /// <summary>Managers: the lots they manage. Staff: the lot they work at.</summary>
    public List<AssignedParkingLotModel> ParkingLots { get; set; } = [];
}

public class AssignedParkingLotModel
{
    public int Id { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }
}

/// <summary>Admin edits a Manager, or a Manager edits one of their Staff.</summary>
public class UpdateEmployeeAccountModel
{
    public string FullName { get; set; }

    public string Email { get; set; }

    public string PhoneNumber { get; set; }
}

/// <summary>Admin sets the full list of lots a Manager manages; lots left out lose this Manager.</summary>
public class AssignParkingLotsModel
{
    public List<int> ParkingLotIds { get; set; } = [];
}

/// <summary>Manager creates a Staff account in one of the lots they manage (UC-09).</summary>
public class CreateStaffAccountModel
{
    /// <summary>Optional; derived from the email when empty.</summary>
    public string UserName { get; set; }

    public string FullName { get; set; }

    public string Email { get; set; }

    public string PhoneNumber { get; set; }

    public string Password { get; set; }

    public string ConfirmPassword { get; set; }

    public int ParkingLotId { get; set; }
}

/// <summary>Manager moves a Staff member to another lot they manage.</summary>
public class AssignStaffParkingLotModel
{
    public int ParkingLotId { get; set; }
}
