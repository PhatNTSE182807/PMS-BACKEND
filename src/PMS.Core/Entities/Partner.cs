using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class Partner
{
    public int Id { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }

    public string PartnerType { get; set; }

    public string TaxCode { get; set; }

    public string ContactName { get; set; }

    public string Phone { get; set; }

    public string Email { get; set; }

    public string Address { get; set; }

    public decimal? RevenueSharePercent { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual ICollection<ParkingLot> ParkingLots { get; set; } = new List<ParkingLot>();
}
