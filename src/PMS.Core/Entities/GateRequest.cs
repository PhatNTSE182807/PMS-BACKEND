using System;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// A customer pressed "Check in" (on a booking) or "Check out" (on a parked vehicle) in the app. The
/// request waits in the lot's queue until a Staff member at the lane opens the barrier or refuses it.
/// </summary>
public class GateRequest
{
    public long Id { get; set; }

    public int ParkingLotId { get; set; }

    /// <summary>"CheckIn" or "CheckOut".</summary>
    public string Kind { get; set; }

    /// <summary>"Waiting", "Completed" (barrier opened), "Refused" (by Staff) or "Cancelled" (by the customer).</summary>
    public string Status { get; set; }

    /// <summary>Identity user who asked (dbo.Users.Id).</summary>
    public string UserId { get; set; }

    public int? CustomerId { get; set; }

    /// <summary>The booking to check in on (CheckIn).</summary>
    public long? ReservationId { get; set; }

    /// <summary>The parked session to close (CheckOut), or the session the check-in created.</summary>
    public long? SessionId { get; set; }

    public Guid? VehicleId { get; set; }

    public string PlateNumber { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? HandledAt { get; set; }

    /// <summary>Staff (or customer, when cancelled) who closed the request.</summary>
    public string HandledBy { get; set; }

    /// <summary>Why Staff refused, shown to the customer.</summary>
    public string Note { get; set; }

    /// <summary>
    /// A walk-in the Admin parking simulation sent to the entry lane (no booking, no account): the Staff checks it in
    /// at the lane like any walk-in, into <see cref="SlotId"/>. UserId is then the Admin who sent it.
    /// </summary>
    public bool IsSimulated { get; set; }

    /// <summary>Vehicle type of a simulated walk-in (the lane's choice for a real one).</summary>
    public int? VehicleTypeId { get; set; }

    /// <summary>Bay the simulated walk-in parks in; walk-ins sent later keep off it while it waits.</summary>
    public int? SlotId { get; set; }

    /// <summary>How long the simulated walk-in stays once in (already in simulated time), before it pays and leaves.</summary>
    public int? SimulatedStaySeconds { get; set; }
}
