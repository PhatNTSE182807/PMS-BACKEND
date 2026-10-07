using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class ShiftSchedule
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }

    public int ParkingLotId { get; set; }

    public int? GateId { get; set; }

    public int ShiftTemplateId { get; set; }

    public DateOnly WorkDate { get; set; }

    public string Status { get; set; }

    public DateTime? CheckInAt { get; set; }

    public DateTime? CheckOutAt { get; set; }

    public string Note { get; set; }

    public string CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }
}
