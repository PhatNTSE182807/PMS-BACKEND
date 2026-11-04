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

    /// <summary>
    /// Legacy: the tariff in force when older bookings were made, as JSON. No longer written or read: a booked
    /// vehicle is charged the price in force when it enters, like any other vehicle.
    /// </summary>
    public string PriceSnapshotJson { get; set; }

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

    /// <summary>
    /// "Scheduled": arrival and departure picked, the bay is booked for that window only.
    /// "PayAsYouGo": arrival picked, leave whenever; the bay stays booked until the vehicle leaves.
    /// The fee is the real parking time either way.
    /// </summary>
    public string BookingMode { get; set; }

    /// <summary>Planned departure of a Scheduled booking (null for pay-as-you-go).</summary>
    public DateTime? DepartureTime { get; set; }

    /// <summary>
    /// When the bay was marked Reserved for this booking. A booking far ahead does not lock the bay
    /// until shortly before arrival; null means this booking does not hold the bay right now.
    /// </summary>
    public DateTime? SlotHeldAt { get; set; }
}
