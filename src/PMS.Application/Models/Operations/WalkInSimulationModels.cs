namespace PMS.Application.Models.Operations;

/// <summary>Admin simulation: send a batch of walk-in vehicles into a lot through the real entry lane.</summary>
public class DeployWalkInsModel
{
    public int LotId { get; set; }

    /// <summary>How many vehicles to send in: from 1 up to the lot's free bays; the batch stops when a zone is full.</summary>
    public int Count { get; set; } = 20;

    /// <summary>
    /// Fill the lot: one vehicle for every bay a walk-in may take (Count is ignored). Car bays get 4- or 7-seat cars
    /// in the ratio of the two car shares, motorbike bays get motorbikes.
    /// </summary>
    public bool FillLot { get; set; }

    /// <summary>Share of 4-seat cars in the batch, 0 to 100.</summary>
    public int CarPercent { get; set; } = 40;

    /// <summary>Share of 7-seat cars in the batch, 0 to 100; with the 4-seat share at most 100, the rest are motorbikes.</summary>
    public int Car7Percent { get; set; } = 15;

    /// <summary>Shortest stay in hours. While the parking simulation is on, one hour lasts one pricing block.</summary>
    public double MinStayHours { get; set; } = 2;

    /// <summary>Longest stay in hours (at most 72).</summary>
    public double MaxStayHours { get; set; } = 4;

    /// <summary>Share of vehicles whose plate cannot be read, 0 to 100 (paper ticket only). The Admin screen no longer offers it.</summary>
    public int NoPlatePercent { get; set; }
}

/// <summary>One simulated walk-in parked in a bay the Admin picked on the 3D model.</summary>
public class DeployWalkInToBayModel
{
    public int LotId { get; set; }

    /// <summary>The bay: free, not held, not booked and not where the model draws a walk-in without a bay.</summary>
    public int SlotId { get; set; }

    /// <summary>CAR_4, CAR_7 or MOTORBIKE; it must fit the bay. Null picks the bay's own type.</summary>
    public string VehicleTypeCode { get; set; }

    /// <summary>Shortest stay in hours. While the parking simulation is on, one hour lasts one pricing block.</summary>
    public double MinStayHours { get; set; } = 2;

    /// <summary>Longest stay in hours (at most 72).</summary>
    public double MaxStayHours { get; set; } = 4;
}

public class DeployWalkInsResultModel
{
    public int Requested { get; set; }

    public int Entered { get; set; }

    /// <summary>Why the batch stopped early (a full zone, a closed lot), in plain words; null when all entered.</summary>
    public string StoppedReason { get; set; }
}

/// <summary>Report a lost ticket for a simulated vehicle, as the driver would at the exit lane.</summary>
public class SimulateLostTicketModel
{
    /// <summary>True: Staff checked the photos and papers and approves on the spot; false: a Manager must decide.</summary>
    public bool ConditionsMet { get; set; }
}

/// <summary>One simulated walk-in still inside a lot.</summary>
public class SimulatedWalkInModel
{
    public long SessionId { get; set; }

    public string SessionCode { get; set; }

    public int LotId { get; set; }

    public string LotName { get; set; }

    /// <summary>Plate read at the entry; empty when the vehicle has no readable plate.</summary>
    public string Plate { get; set; }

    public bool HasPlate { get; set; }

    public string VehicleTypeName { get; set; }

    public string ZoneName { get; set; }

    public DateTime EntryTime { get; set; }

    /// <summary>When the simulation drives it out.</summary>
    public DateTime? PlannedExitAt { get; set; }

    public string Status { get; set; }

    public bool LostTicket { get; set; }
}

public class WalkInSimulationOverviewModel
{
    /// <summary>Simulated vehicles still inside, newest entry first.</summary>
    public List<SimulatedWalkInModel> Inside { get; set; } = [];

    /// <summary>Simulated vehicles that paid and left.</summary>
    public int CompletedCount { get; set; }

    /// <summary>What the simulated vehicles paid (parking fee, lost-ticket fines, late fees).</summary>
    public decimal Revenue { get; set; }

    /// <summary>Bays of the lot in active zones; null when no lot is chosen.</summary>
    public int? TotalBays { get; set; }

    /// <summary>Bays of the lot a vehicle can park in now: the most vehicles one batch can send; null when no lot is chosen.</summary>
    public int? FreeBays { get; set; }
}

public class WalkInCheckOutResultModel
{
    public int CheckedOut { get; set; }

    /// <summary>Vehicles that could not leave yet (a lost ticket waiting for a Manager, for instance).</summary>
    public int Skipped { get; set; }
}

/// <summary>
/// A simulated walk-in now stands in a booked bay: a case is open for the lot's Staff and the booking's customer was
/// told their bay is taken.
/// </summary>
public class BookedBayTakenModel
{
    public long SessionId { get; set; }

    public string Plate { get; set; }

    public string BayCode { get; set; }

    public long ReservationId { get; set; }

    public string ReservationCode { get; set; }

    /// <summary>The case opened for the lot's Staff (Exceptions & Alerts); null when one was already open.</summary>
    public long? CaseId { get; set; }
}
