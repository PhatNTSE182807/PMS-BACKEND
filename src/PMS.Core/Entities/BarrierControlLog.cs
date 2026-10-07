using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class BarrierControlLog
{
    public long Id { get; set; }

    public int DeviceId { get; set; }

    public int? GateId { get; set; }

    public string Command { get; set; }

    public string Source { get; set; }

    public string UserId { get; set; }

    public long? SessionId { get; set; }

    public string Reason { get; set; }

    public bool Succeeded { get; set; }

    public DateTime CreatedAt { get; set; }
}
