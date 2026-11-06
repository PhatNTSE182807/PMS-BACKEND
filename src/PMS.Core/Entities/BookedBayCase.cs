using System;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// A walk-in parked in the bay of a confirmed booking (BR-08). Opened as soon as the walk-in is recorded in that bay;
/// the lot's Staff either move the booking to another free bay of its type, or, when none is left, cancel it with the
/// deposit refunded and a compensation paid into the customer's XXIV wallet. Closed on its own when the walk-in
/// leaves first or the booking ends another way. Never deleted.
/// </summary>
public class BookedBayCase
{
    public long Id { get; set; }

    public int ParkingLotId { get; set; }

    public long ReservationId { get; set; }

    /// <summary>The booked bay the walk-in took.</summary>
    public int SlotId { get; set; }

    /// <summary>The walk-in's session.</summary>
    public long SessionId { get; set; }

    /// <summary>Open, Reassigned, Compensated or Closed (BookedBayCaseStatus).</summary>
    public string Status { get; set; }

    /// <summary>Bay the booking was moved to (Reassigned).</summary>
    public int? NewSlotId { get; set; }

    /// <summary>Deposit refunded to the wallet (Compensated).</summary>
    public decimal DepositRefunded { get; set; }

    /// <summary>Compensation paid to the wallet (Compensated), from the lot's pricing rule of the vehicle type.</summary>
    public decimal CompensationPaid { get; set; }

    /// <summary>The Payment row (Purpose "Compensation") that records the compensation against the lot.</summary>
    public long? CompensationPaymentId { get; set; }

    /// <summary>Why it was resolved or closed, in words.</summary>
    public string ResolutionNote { get; set; }

    /// <summary>Who resolved it (dbo.Users.Id), or "System".</summary>
    public string ResolvedBy { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
