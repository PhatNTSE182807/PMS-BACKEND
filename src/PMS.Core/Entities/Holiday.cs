using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class Holiday
{
    public int Id { get; set; }

    public DateOnly HolidayDate { get; set; }

    public string Name { get; set; }

    public bool IsRecurringYearly { get; set; }
}
