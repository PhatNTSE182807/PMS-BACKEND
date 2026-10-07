using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class ApiKey
{
    public int Id { get; set; }

    public string Name { get; set; }

    public string KeyPrefix { get; set; }

    public string KeyHash { get; set; }

    public int? PartnerId { get; set; }

    public string Scopes { get; set; }

    public int? RateLimitPerMinute { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public DateTime? LastUsedAt { get; set; }

    public bool IsActive { get; set; }

    public string CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? RevokedAt { get; set; }
}
