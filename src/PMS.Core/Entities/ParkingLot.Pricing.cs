using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// Deferred price updates: a Manager's price change is applied at once only when no vehicle is inside;
/// otherwise it waits in <see cref="PendingPriceChange"/> until the last vehicle leaves.
/// </summary>
public partial class ParkingLot
{
    /// <summary>
    /// Vehicles inside the lot: sessions in <c>SessionStatus.Open</c>. Moves with every entry and exit, in the
    /// same transaction as the session change. The lot is "empty" for a price change when this is 0.
    /// </summary>
    public int OccupiedBays { get; set; }

    /// <summary>
    /// Concurrency token of the lot. Entry, exit and price changes all write this row, so two of them can
    /// never decide on the same state: the later save fails and is retried against the new state.
    /// Initialised so entities created in code (and in the in-memory database) satisfy the required column.
    /// </summary>
    public byte[] RowVersion { get; set; } = [];

    public virtual ICollection<PendingPriceChange> PendingPriceChanges { get; set; } = new List<PendingPriceChange>();
}
