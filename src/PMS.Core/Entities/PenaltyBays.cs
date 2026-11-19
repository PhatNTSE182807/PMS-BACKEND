using System;

namespace PMS.DataAccess.Persistence.Entities;

/// <summary>
/// Penalty bays (team rule of 2026-11-15): every level has a few bays set apart for vehicles that break the rules,
/// a walk-in parked in a booked bay or a booked vehicle that stayed past its booking. Staff tow the vehicle there so
/// the booked customer gets the bay they picked. Penalty bays are not part of a zone's capacity or counter.
/// </summary>
public partial class Slot
{
    /// <summary>Standard (bookable / walk-in) or Penalty. See SlotKind.</summary>
    public string Kind { get; set; } = "Standard";
}

public partial class ParkingSession
{
    /// <summary>Why the vehicle was towed to a penalty bay: WrongBay (walk-in in a booked bay) or Overstay. Null when it never was.</summary>
    public string PenaltyReason { get; set; }

    /// <summary>The fixed penalty owed at check-out on top of the parking fee, copied from the pricing rule when the vehicle was towed.</summary>
    public decimal PenaltyFee { get; set; }

    /// <summary>When Staff towed the vehicle out of the booked bay.</summary>
    public DateTime? RelocatedAt { get; set; }

    /// <summary>The booked bay the vehicle was towed out of.</summary>
    public int? RelocatedFromSlotId { get; set; }
}

public partial class PricingRule
{
    /// <summary>
    /// Fixed penalty charged at check-out to a vehicle towed out of a booked bay (a walk-in in the wrong bay or a booking
    /// that overstayed). Not a parking price: changing it never notifies customers of a price change.
    /// </summary>
    public decimal PenaltyFee { get; set; }

    /// <summary>Default penalty for a car rule (4- or 7-seat), in VND; the Manager can change it per rule.</summary>
    public const decimal DefaultCarPenalty = 200_000;

    /// <summary>Default penalty for a motorbike rule, in VND; the Manager can change it per rule.</summary>
    public const decimal DefaultMotorbikePenalty = 50_000;

    /// <summary>The default penalty for a vehicle type code: cars 200,000 VND, motorbikes 50,000 VND, others 0.</summary>
    public static decimal DefaultPenaltyFor(string vehicleTypeCode) =>
        vehicleTypeCode?.StartsWith("CAR", StringComparison.OrdinalIgnoreCase) == true ? DefaultCarPenalty
        : vehicleTypeCode == "MOTORBIKE" ? DefaultMotorbikePenalty
        : 0;
}
