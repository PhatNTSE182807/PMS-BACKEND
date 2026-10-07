using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class AuditLog
{
    public long Id { get; set; }

    public string UserId { get; set; }

    public string UserName { get; set; }

    public string Action { get; set; }

    public string EntityName { get; set; }

    public string EntityId { get; set; }

    public int? ParkingLotId { get; set; }

    public string OldValues { get; set; }

    public string NewValues { get; set; }

    public string IpAddress { get; set; }

    public string UserAgent { get; set; }

    public DateTime CreatedAt { get; set; }
}
