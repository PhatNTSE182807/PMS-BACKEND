using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class SessionViolation
{
    public long Id { get; set; }

    public long SessionId { get; set; }

    public string ViolationType { get; set; }

    public decimal FineAmount { get; set; }

    public string Note { get; set; }

    public string ReportedBy { get; set; }

    public DateTime ReportedAt { get; set; }

    public bool IsResolved { get; set; }
}
