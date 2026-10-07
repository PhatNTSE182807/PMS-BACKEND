using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class ShiftCashCount
{
    public int Id { get; set; }

    public long ShiftId { get; set; }

    public decimal Denomination { get; set; }

    public int Quantity { get; set; }
}
