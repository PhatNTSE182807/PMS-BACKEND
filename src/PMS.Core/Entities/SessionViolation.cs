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

    /// <summary>Lost-ticket cases: Pending, Approved or Rejected (LostTicketCaseStatus).</summary>
    public string Status { get; set; }

    /// <summary>Lost-ticket cases: Conditional (on the spot) or Manager (LostTicketApproval).</summary>
    public string ApprovalMode { get; set; }

    /// <summary>User id of who approved or rejected the case.</summary>
    public string DecidedBy { get; set; }

    public DateTime? DecidedAt { get; set; }

    public string DecisionNote { get; set; }
}
