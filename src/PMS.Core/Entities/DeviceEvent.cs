using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class DeviceEvent
{
    public long Id { get; set; }

    public int DeviceId { get; set; }

    public string EventType { get; set; }

    public string Severity { get; set; }

    public string Message { get; set; }

    public string PayloadJson { get; set; }

    public DateTime OccurredAt { get; set; }
}
