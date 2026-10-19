namespace PMS.DataAccess.Persistence.Entities;

/// <summary>A deposit invoice belongs to a reservation instead of a session.</summary>
public partial class Invoice
{
    public long? ReservationId { get; set; }
}
