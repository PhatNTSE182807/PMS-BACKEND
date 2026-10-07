using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class RefreshToken
{
    public long Id { get; set; }

    public string UserId { get; set; }

    public string TokenHash { get; set; }

    public string DeviceInfo { get; set; }

    public string IpAddress { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    public string RevokedReason { get; set; }

    public string ReplacedByTokenHash { get; set; }
}
