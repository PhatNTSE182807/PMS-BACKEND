using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class LoyaltyTransaction
{
    public long Id { get; set; }

    public int CustomerId { get; set; }

    public int Points { get; set; }

    public string TxnType { get; set; }

    public string ReferenceType { get; set; }

    public string ReferenceId { get; set; }

    public int BalanceAfter { get; set; }

    public string Description { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ExpiresAt { get; set; }
}
