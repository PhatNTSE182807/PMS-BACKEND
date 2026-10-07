using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class ShiftTemplate
{
    public int Id { get; set; }

    public int ParkingLotId { get; set; }

    public string Name { get; set; }

    public TimeOnly StartTime { get; set; }

    public TimeOnly EndTime { get; set; }

    public bool CrossesMidnight { get; set; }

    public bool IsActive { get; set; }
}
