using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class Invoice
{
    public long Id { get; set; }

    public string InvoiceNumber { get; set; }

    public long? SessionId { get; set; }

    public long? SubscriptionId { get; set; }

    public int? CustomerId { get; set; }

    public int ParkingLotId { get; set; }

    public decimal SubTotal { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public string Status { get; set; }

    public DateTime? DueDate { get; set; }

    public DateTime? PaidAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<Payment> Payments { get; set; } = new List<Payment>();

    public virtual ParkingSession Session { get; set; }
}
