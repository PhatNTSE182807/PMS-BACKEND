using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class Payment
{
    public long Id { get; set; }

    public string PaymentCode { get; set; }

    public long InvoiceId { get; set; }

    public string PaymentMethod { get; set; }

    public string GatewayProvider { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; }

    public string Status { get; set; }

    public string TransactionRef { get; set; }

    public string PayloadResponse { get; set; }

    public DateTime? PaidAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Invoice Invoice { get; set; }
}
