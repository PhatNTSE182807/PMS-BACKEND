using System;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// Booking fields of a reservation (SRS FR-RSV-02..09, section 8 "Reservation"), kept apart from the
/// scaffolded columns. <see cref="Reservation.StartTime"/> is the expected arrival time and
/// <see cref="Reservation.EndTime"/> the end of the hold (arrival + hold time, BR-02).
/// </summary>
public partial class Reservation
{
    /// <summary>Identity user who booked (dbo.Users.Id).</summary>
    public string UserId { get; set; }

    public int? VehicleTypeId { get; set; }

    /// <summary>The slot is held until then; an unpaid deposit after it expires the booking (BR-06).</summary>
    public DateTime? DepositDueAt { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    public DateTime? CheckedInAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string CancelReason { get; set; }

    /// <summary>"User", "Lot" (FR-RSV-09) or "System".</summary>
    public string CancelledBy { get; set; }

    /// <summary>Deposit returned to the customer so far (cancellation, lot cancellation, residual BR-03).</summary>
    public decimal RefundAmount { get; set; }

    /// <summary>Times the arrival time or slot was changed (FR-RSV-03).</summary>
    public int ChangeCount { get; set; }

    /// <summary>When the "arriving soon" reminder went out (FR-RSV-10).</summary>
    public DateTime? ReminderSentAt { get; set; }

    /// <summary>When the "hold ends soon" reminder went out (FR-RSV-10).</summary>
    public DateTime? HoldReminderSentAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
