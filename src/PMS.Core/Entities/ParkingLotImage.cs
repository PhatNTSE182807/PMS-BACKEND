using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class ParkingLotImage
{
    public int Id { get; set; }

    public int ParkingLotId { get; set; }

    public string ImageUrl { get; set; }

    public string Caption { get; set; }

    public int SortOrder { get; set; }

    public bool IsPrimary { get; set; }
}
