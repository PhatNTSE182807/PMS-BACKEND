namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// The lot's occupancy counter and concurrency token. A price change saves this row too, which serialises it with
/// vehicles entering: an entry racing a new price is redone with that price.
/// </summary>
public partial class ParkingLot
{
    /// <summary>
    /// Vehicles inside the lot: sessions in <c>SessionStatus.Open</c>. Moves with every entry and exit, in the
    /// same transaction as the session change.
    /// </summary>
    public int OccupiedBays { get; set; }

    /// <summary>
    /// Concurrency token of the lot. Entry, exit and price changes all write this row, so two of them can
    /// never decide on the same state: the later save fails and is retried against the new state.
    /// Initialised so entities created in code (and in the in-memory database) satisfy the required column.
    /// </summary>
    public byte[] RowVersion { get; set; } = [];
}
