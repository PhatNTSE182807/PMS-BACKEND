using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class SubscriptionRenewal
{
    public long Id { get; set; }

    public long SubscriptionId { get; set; }

    public DateOnly OldEndDate { get; set; }

    public DateOnly NewEndDate { get; set; }

    public decimal AmountPaid { get; set; }

    public long? PaymentId { get; set; }

    public DateTime RenewedAt { get; set; }

    public string RenewedBy { get; set; }
}
