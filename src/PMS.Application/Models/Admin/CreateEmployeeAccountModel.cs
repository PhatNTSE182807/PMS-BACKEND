namespace PMS.Application.Models.Admin;

public class CreateEmployeeAccountModel
{
    public string UserName { get; set; }

    public string FullName { get; set; }

    public string Email { get; set; }

    public string Password { get; set; }

    public string PhoneNumber { get; set; }
 
    public string AssignedParkingLot { get; set; }

    /// <summary>POST /admin/managers only: lots the new Manager takes over straight away.</summary>
    public List<int> ParkingLotIds { get; set; }
}
