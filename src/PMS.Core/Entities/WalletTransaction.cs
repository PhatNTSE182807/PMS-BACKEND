using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class WalletTransaction
{
    public long Id { get; set; }

    public int WalletId { get; set; }

    public string TxnType { get; set; }

    public decimal Amount { get; set; }

    public decimal BalanceBefore { get; set; }

    public decimal BalanceAfter { get; set; }

    public long? PaymentId { get; set; }

    public string ReferenceType { get; set; }

    public string ReferenceId { get; set; }

    public string Description { get; set; }

    public DateTime CreatedAt { get; set; }

    public string CreatedBy { get; set; }
}
