namespace PMS.Application.Models.Operations;

/// <summary>
/// A vehicle of the customer that is inside a lot right now, with everything the app's "active
/// session" card shows: where it is parked, the running fee, the tariff, the open payment and the
/// exit pass.
/// </summary>
public class CurrentSessionModel
{
    public SessionResponseModel Session { get; set; }

    public string LotName { get; set; }
    public string LotAddress { get; set; }
    public string LotDistrict { get; set; }

    /// <summary>Map position of the lot, for the directions map; null when the Admin has not set it.</summary>
    public decimal? LotLatitude { get; set; }
    public decimal? LotLongitude { get; set; }

    /// <summary>Name of the entry lane, e.g. "Entry lane 1".</summary>
    public string EntryGateName { get; set; }

    /// <summary>How the entry was identified: "Plate" (ANPR), "Reservation" (booking QR or code) or "Manual".</summary>
    public string EntryMethod { get; set; }

    /// <summary>Brand and model of the vehicle, e.g. "Toyota Camry".</summary>
    public string VehicleLabel { get; set; }

    /// <summary>"Scheduled" or "PayAsYouGo" when the vehicle came on a booking.</summary>
    public string BookingMode { get; set; }

    /// <summary>Planned departure of a Scheduled booking.</summary>
    public DateTime? PlannedDeparture { get; set; }

    public int ElapsedMinutes { get; set; }

    /// <summary>Parking fee so far (before the deposit and payments).</summary>
    public decimal FeeSoFar { get; set; }

    public decimal DepositApplied { get; set; }

    public decimal AlreadyPaid { get; set; }

    /// <summary>What paying now would cost (fee minus deposit and payments).</summary>
    public decimal AmountDue { get; set; }

    public CurrentTariffModel Tariff { get; set; }

    /// <summary>Open VNPay / MoMo payment started from the app, if any.</summary>
    public PaymentResponseModel PendingPayment { get; set; }

    public ExitPassModel ExitPass { get; set; }
}

/// <summary>The price list the running fee follows.</summary>
public class CurrentTariffModel
{
    /// <summary>Free when the whole stay is at most this long.</summary>
    public int GraceMinutes { get; set; }

    /// <summary>Length of one paid block (60 = per started hour).</summary>
    public int BlockMinutes { get; set; }

    public decimal BlockPrice { get; set; }

    /// <summary>Cap per calendar day, if any.</summary>
    public decimal? MaxDailyFee { get; set; }
}

/// <summary>
/// The exit pass the customer shows at the exit lane. Every vehicle inside a lot has one from the
/// moment it checked in: the lane scans it to find the session, then charges what is still due.
/// After a prepayment it also proves the fee is paid until <see cref="ValidUntil"/> (works offline).
/// </summary>
public class ExitPassModel
{
    public long SessionId { get; set; }

    public string SessionCode { get; set; }

    /// <summary>Signed token to render as a QR code.</summary>
    public string Token { get; set; }

    /// <summary>The fee is paid: the barrier opens without charging (until <see cref="ValidUntil"/>).</summary>
    public bool Prepaid { get; set; }

    /// <summary>End of the Exit Window after a prepayment; null when the fee is paid at the exit.</summary>
    public DateTime? ValidUntil { get; set; }

    /// <summary>Still to pay at the exit lane (0 when prepaid or free).</summary>
    public decimal AmountDue { get; set; }
}
