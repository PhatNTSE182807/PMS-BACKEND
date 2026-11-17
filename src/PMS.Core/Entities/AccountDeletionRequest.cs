using System;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// A customer asked for their account to be deleted (team addition, 2026-11-17). The request only tells the Admins:
/// an Admin decides and locks the account from the customer list. Rows are never deleted; locking the account
/// completes the request and the customer can cancel it while it is pending.
/// </summary>
public class AccountDeletionRequest
{
    public long Id { get; set; }

    /// <summary>The customer's account (Identity user id).</summary>
    public string UserId { get; set; }

    /// <summary>Optional reason the customer gave.</summary>
    public string Reason { get; set; }

    /// <summary>One of <c>AccountDeletionRequestStatuses</c>: Pending, Completed or Cancelled.</summary>
    public string Status { get; set; }

    public DateTime RequestedAt { get; set; }

    /// <summary>Who closed the request: the Admin who locked the account, or the customer who cancelled it.</summary>
    public string ClosedBy { get; set; }

    public DateTime? ClosedAt { get; set; }
}
