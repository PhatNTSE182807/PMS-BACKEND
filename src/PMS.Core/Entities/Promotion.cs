using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class Promotion
{
    public int Id { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public string PromotionType { get; set; }

    public decimal DiscountValue { get; set; }

    public decimal? MaxDiscountAmount { get; set; }

    public decimal? MinFeeAmount { get; set; }

    public DateTime ValidFrom { get; set; }

    public DateTime ValidTo { get; set; }

    public int? TotalUsageLimit { get; set; }

    public int? PerCustomerLimit { get; set; }

    public int UsedCount { get; set; }

    public int? ParkingLotId { get; set; }

    public int? PartnerId { get; set; }

    public bool RequiresPartnerConfirmation { get; set; }

    public string Status { get; set; }

    public string CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
