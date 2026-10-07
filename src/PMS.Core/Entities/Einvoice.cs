using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class Einvoice
{
    public long Id { get; set; }

    public long InvoiceId { get; set; }

    public string Provider { get; set; }

    public string TemplateCode { get; set; }

    public string Series { get; set; }

    public string EinvoiceNumber { get; set; }

    public string TransactionUuid { get; set; }

    public string PdfUrl { get; set; }

    public string Status { get; set; }

    public string ErrorMessage { get; set; }

    public DateTime? IssuedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
