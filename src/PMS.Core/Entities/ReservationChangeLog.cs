using System;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>History of a reservation's changes and status moves (SRS section 8 "ReservationChangeLog").</summary>
public class ReservationChangeLog
{
    public long Id { get; set; }

    public long ReservationId { get; set; }

    /// <summary>Created, Confirmed, ArrivalChanged, SlotChanged, Cancelled, LotCancelled, Expired, NoShow, CheckedIn, Completed.</summary>
    public string Action { get; set; }

    public string OldValue { get; set; }

    public string NewValue { get; set; }

    public string ChangedBy { get; set; }

    public DateTime ChangedAt { get; set; }
}
