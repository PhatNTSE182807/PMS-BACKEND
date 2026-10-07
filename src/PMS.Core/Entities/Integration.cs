using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class Integration
{
    public int Id { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }

    public string IntegrationType { get; set; }

    public string Provider { get; set; }

    public string BaseUrl { get; set; }

    public string ConfigJson { get; set; }

    public bool IsSandbox { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
