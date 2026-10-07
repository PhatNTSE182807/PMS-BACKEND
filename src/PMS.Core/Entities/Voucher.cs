using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class Voucher
{
    public int Id { get; set; }

    public int PromotionId { get; set; }

    public string VoucherCode { get; set; }

    public string Status { get; set; }

    public int? AssignedCustomerId { get; set; }

    public int? IssuedByPartnerId { get; set; }

    public DateTime IssuedAt { get; set; }

    public DateTime? UsedAt { get; set; }

    public long? UsedInSessionId { get; set; }

    public long? UsedInInvoiceId { get; set; }
}
