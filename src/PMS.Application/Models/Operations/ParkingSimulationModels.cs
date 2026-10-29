namespace PMS.Application.Models.Operations;

/// <summary>
/// Parking simulation: an Admin demo mode that runs the clock-based rules in seconds instead of hours and
/// minutes, so a whole stay, a booking hold or an exit window can be shown in a few seconds. It applies to
/// every lot. While it is on, one pricing block of every rule lasts <see cref="BlockSeconds"/> (the block
/// price stays the same) and there is no daily cap; turning it off goes back to the normal rules.
/// The object is replaced as a whole, never edited in place, so readers always see one consistent state.
/// </summary>
public class ParkingSimulationSettings
{
    /// <summary>Shortest and longest value, in seconds, of every simulated duration.</summary>
    public const int MinSeconds = 1;
    public const int MaxSeconds = 3600;

    public bool Enabled { get; set; }

    /// <summary>Length of one pricing block (e.g. 30,000 VND per 5 seconds instead of per hour).</summary>
    public int BlockSeconds { get; set; } = 5;

    /// <summary>How long a booking with a paid deposit holds the bay after the arrival time.</summary>
    public int DepositHoldSeconds { get; set; } = 15;

    /// <summary>How long a booking without a deposit holds the bay after the arrival time.</summary>
    public int HoldSeconds { get; set; } = 15;

    /// <summary>Time to pay the deposit after booking.</summary>
    public int DepositPaymentSeconds { get; set; } = 15;

    /// <summary>Check-in opens this long before the arrival time.</summary>
    public int EarlyArrivalSeconds { get; set; } = 15;

    /// <summary>Cancelling at least this long before arrival refunds the deposit; also the change deadline.</summary>
    public int CancelRefundSeconds { get; set; } = 15;

    /// <summary>Time to leave after paying at the counter or in the app.</summary>
    public int ExitWindowSeconds { get; set; } = 15;

    public ParkingSimulationSettings Copy() => (ParkingSimulationSettings)MemberwiseClone();
}

/// <summary>What the Admin saves on the Parking Simulation screen.</summary>
public class UpdateParkingSimulationModel
{
    public bool Enabled { get; set; }
    public int BlockSeconds { get; set; }
    public int DepositHoldSeconds { get; set; }
    public int HoldSeconds { get; set; }
    public int DepositPaymentSeconds { get; set; }
    public int EarlyArrivalSeconds { get; set; }
    public int CancelRefundSeconds { get; set; }
    public int ExitWindowSeconds { get; set; }
}

/// <summary>The normal (non-simulated) values, shown next to each simulated one.</summary>
public class ParkingSimulationDefaultsModel
{
    public int DepositHoldMinutes { get; set; }
    public int HoldMinutes { get; set; }
    public int DepositPaymentMinutes { get; set; }
    public int EarlyArrivalMinutes { get; set; }
    public int CancelRefundMinutes { get; set; }
    public int ExitWindowMinutes { get; set; }
}

/// <summary>The simulation as the Admin screen shows it.</summary>
public class ParkingSimulationResponseModel
{
    public bool Enabled { get; set; }
    public int BlockSeconds { get; set; }
    public int DepositHoldSeconds { get; set; }
    public int HoldSeconds { get; set; }
    public int DepositPaymentSeconds { get; set; }
    public int EarlyArrivalSeconds { get; set; }
    public int CancelRefundSeconds { get; set; }
    public int ExitWindowSeconds { get; set; }
    public ParkingSimulationDefaultsModel Normal { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string UpdatedBy { get; set; }
}
