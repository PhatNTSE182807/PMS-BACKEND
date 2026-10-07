using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class Wallet
{
    public int Id { get; set; }

    public int CustomerId { get; set; }

    public decimal Balance { get; set; }

    public string Currency { get; set; }

    public string Status { get; set; }

    public DateTime UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; }
}
