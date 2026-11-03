namespace PMS.Application.Models.Operations;

/// <summary>Admin simulation: send a batch of walk-in vehicles into a lot through the real entry lane.</summary>
public class DeployWalkInsModel
{
    public const int MaxCount = 500;

    public int LotId { get; set; }

    /// <summary>How many vehicles to send in (1 to <see cref="MaxCount"/>); the batch stops when a zone is full.</summary>
    public int Count { get; set; } = 20;

    /// <summary>Share of cars in the batch, 0 to 100; the rest are motorbikes.</summary>
    public int CarPercent { get; set; } = 50;

    /// <summary>Shortest stay in hours. While the parking simulation is on, one hour lasts one pricing block.</summary>
    public double MinStayHours { get; set; } = 2;

    /// <summary>Longest stay in hours (at most 72).</summary>
    public double MaxStayHours { get; set; } = 4;

    /// <summary>Share of vehicles whose plate cannot be read, 0 to 100 (paper ticket only).</summary>
    public int NoPlatePercent { get; set; }
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
}

public class WalkInCheckOutResultModel
{
    public int CheckedOut { get; set; }

    /// <summary>Vehicles that could not leave yet (a lost ticket waiting for a Manager, for instance).</summary>
    public int Skipped { get; set; }
}

/// <summary>A simulated walk-in now stands in a booked bay; the booking's customer meets it at check-in.</summary>
public class BookedBayTakenModel
{
    public long SessionId { get; set; }

    public string Plate { get; set; }

    public string BayCode { get; set; }

    public long ReservationId { get; set; }

    public string ReservationCode { get; set; }
}
