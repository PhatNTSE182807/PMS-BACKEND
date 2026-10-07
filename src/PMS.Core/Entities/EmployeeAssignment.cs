using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class EmployeeAssignment
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }

    public int ParkingLotId { get; set; }

    public int? GateId { get; set; }

    public bool IsPrimary { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }
}
