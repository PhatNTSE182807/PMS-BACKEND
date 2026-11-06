using System;
using System.Collections.Generic;

namespace PMS.Application.Models.Operations;

/// <summary>
/// A walk-in parked in a booked bay, as the lot's Staff see it on the Exceptions screen: the booking, the walk-in, the
/// money at stake and the free bays the booking can move to.
/// </summary>
public class BookedBayCaseModel
{
    public long Id { get; set; }

    /// <summary>Open, Reassigned, Compensated or Closed.</summary>
    public string Status { get; set; }

    public int ParkingLotId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ResolvedAt { get; set; }

    /// <summary>Name of who resolved it, or "System".</summary>
    public string ResolvedByName { get; set; }

    public string ResolutionNote { get; set; }

    public long ReservationId { get; set; }

    public string ReservationCode { get; set; }

    public string ReservationStatus { get; set; }

    public DateTime ArrivalTime { get; set; }

    public DateTime HoldEndsAt { get; set; }

    public string CustomerName { get; set; }

    /// <summary>Plate of the booked vehicle.</summary>
    public string BookedPlate { get; set; }

    public int? VehicleTypeId { get; set; }

    public string VehicleTypeName { get; set; }

    public int BayId { get; set; }

    public string BayCode { get; set; }

    public int? NewBayId { get; set; }

    public string NewBayCode { get; set; }

    public long WalkInSessionId { get; set; }

    public string WalkInSessionCode { get; set; }

    public string WalkInPlate { get; set; }

    public DateTime WalkInEntryTime { get; set; }

    /// <summary>The walk-in is still inside the lot.</summary>
    public bool WalkInInside { get; set; }

    /// <summary>Deposit still held for the booking (what a cancellation refunds).</summary>
    public decimal DepositHeld { get; set; }

    public decimal DepositRefunded { get; set; }

    /// <summary>What a cancellation pays now, from the lot's pricing rule of the vehicle type.</summary>
    public decimal CompensationAmount { get; set; }

    public decimal CompensationPaid { get; set; }

    /// <summary>Free bays of the booked vehicle type the booking can move to (open cases only). Empty: cancel and compensate.</summary>
    public List<BookedBayOptionModel> FreeBays { get; set; } = [];
}

public class BookedBayOptionModel
{
    public int SlotId { get; set; }

    public string Code { get; set; }

    public string ZoneName { get; set; }
}

/// <summary>Move the booking to another free bay of its type.</summary>
public class ReassignBookedBayModel
{
    public int SlotId { get; set; }

    public string Note { get; set; }
}

/// <summary>No bay is left: cancel the booking, refund the deposit and pay the compensation into the wallet.</summary>
public class CompensateBookedBayModel
{
    public string Note { get; set; }
}
