using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class CorporateAccount
{
    public int Id { get; set; }

    public string CompanyName { get; set; }

    public string TaxCode { get; set; }

    public string Address { get; set; }

    public string ContactName { get; set; }

    public string Phone { get; set; }

    public string Email { get; set; }

    public string BillingEmail { get; set; }

    public bool ConsolidatedInvoice { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
