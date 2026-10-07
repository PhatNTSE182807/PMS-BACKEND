using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class OtpCode
{
    public long Id { get; set; }

    public string UserId { get; set; }

    public string Destination { get; set; }

    public string Channel { get; set; }

    public string Purpose { get; set; }

    public string CodeHash { get; set; }

    public int AttemptCount { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? ConsumedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
