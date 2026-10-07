using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class LinkedPaymentAccount
{
    public int Id { get; set; }

    public int CustomerId { get; set; }

    public string Provider { get; set; }

    public string ProviderTokenRef { get; set; }

    public string MaskedInfo { get; set; }

    public bool IsDefault { get; set; }

    public bool AutoPayEnabled { get; set; }

    public string Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? RevokedAt { get; set; }
}
