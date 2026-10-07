using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class UserConsent
{
    public int Id { get; set; }

    public string UserId { get; set; }

    public string ConsentType { get; set; }

    public bool IsGranted { get; set; }

    public string Version { get; set; }

    public DateTime GrantedAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    public string IpAddress { get; set; }
}
