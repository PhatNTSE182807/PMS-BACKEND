namespace PMS.Application.Models.Operations;

/// <summary>
/// Configurable business rules of reservations, gates and pricing (SRS section 5, BR-01..BR-22), read
/// from the "Parking" configuration section. The defaults are the SRS defaults.
/// </summary>
public class ParkingOptions
{
    public const string Section = "Parking";

    public ReservationRules Reservations { get; set; } = new();

    public GateRules Gate { get; set; } = new();

    public WalletRules Wallet { get; set; } = new();

    /// <summary>
    /// Tariffs used when a lot has no active PricingRule for the vehicle type. When empty the SRS fee
    /// example is used for cars plus small flat-ish tariffs for two-wheelers (see <see cref="DefaultTariffs"/>).
    /// </summary>
    public DefaultTariff[] Tariffs { get; set; } = [];

    /// <summary>
    /// Master key for signed reservation QR codes, entry tickets and Exit Passes (NFR-16). Each lot signs
    /// with a key derived from it. When unset a random key is used, so tokens stop working after a restart.
    /// </summary>
    public string TicketSigningKey { get; set; }

    /// <summary>Calendar days for daily caps and VNPay dates (BR-22); the lot's own time zone wins when set.</summary>
    public string TimeZoneId { get; set; } = "SE Asia Standard Time";

    /// <summary>The tariffs in force: the configured ones, or the SRS defaults.</summary>
    public IReadOnlyList<DefaultTariff> EffectiveTariffs => Tariffs is { Length: > 0 } ? Tariffs : DefaultTariffs;

    /// <summary>SRS fee example for cars (15 min free, 20,000 VND per hour, 150,000 VND per calendar day).</summary>
    public static readonly DefaultTariff[] DefaultTariffs =
    [
        new() { VehicleTypeCode = "CAR_4", GraceMinutes = 15, BlockMinutes = 60, BlockPrice = 20_000, MaxDailyFee = 150_000, LostTicketFee = 100_000 },
        new() { VehicleTypeCode = "CAR_7", GraceMinutes = 15, BlockMinutes = 60, BlockPrice = 20_000, MaxDailyFee = 150_000, LostTicketFee = 100_000 },
        new() { VehicleTypeCode = "MOTORBIKE", GraceMinutes = 15, BlockMinutes = 240, BlockPrice = 5_000, MaxDailyFee = 20_000, LostTicketFee = 50_000 }
    ];
}

/// <summary>Reservation policy (FR-RSV-06): BR-01 to BR-07.</summary>
public class ReservationRules
{
    /// <summary>BR-01 (changed): the arrival can be picked up to this many days ahead, never in the past.</summary>
    public int MaxLeadDays { get; set; } = 30;

    /// <summary>The bay turns Reserved (and leaves the free count) this many minutes before arrival; earlier it stays free.</summary>
    public int HoldAheadMinutes { get; set; } = 120;

    /// <summary>Shortest window of a Scheduled booking.</summary>
    public int MinStayMinutes { get; set; } = 30;

    /// <summary>Longest window of a Scheduled booking.</summary>
    public int MaxStayHours { get; set; } = 24;

    /// <summary>BR-06: the deposit must be paid within this many minutes of picking the slot.</summary>
    public int DepositMinutes { get; set; } = 10;

    /// <summary>BR-02: check-in opens this many minutes before the arrival time.</summary>
    public int EarlyArrivalMinutes { get; set; } = 15;

    /// <summary>
    /// BR-02: a booking made without a deposit holds the slot this many minutes after the arrival time
    /// (then No-show, BR-05).
    /// </summary>
    public int HoldMinutes { get; set; } = 15;

    /// <summary>
    /// Team change to BR-02: a booking with a paid deposit holds the slot longer, this many minutes
    /// after the arrival time.
    /// </summary>
    public int DepositHoldMinutes { get; set; } = 30;

    /// <summary>How long the slot is held after arrival for a booking with or without a deposit.</summary>
    public int HoldMinutesFor(bool withDeposit) => withDeposit ? DepositHoldMinutes : HoldMinutes;

    /// <summary>BR-04: cancelling at least this many minutes before arrival refunds 100 %; later refunds nothing. Also the change deadline.</summary>
    public int CancelRefundMinutes { get; set; } = 30;

    /// <summary>BR-07: active reservations per account.</summary>
    public int MaxActivePerAccount { get; set; } = 2;

    /// <summary>BR-07: active reservations per vehicle.</summary>
    public int MaxActivePerVehicle { get; set; } = 1;

    /// <summary>FR-RSV-10: remind this many minutes before the arrival time.</summary>
    public int ReminderBeforeMinutes { get; set; } = 15;

    /// <summary>FR-RSV-10: remind this many minutes before the hold ends.</summary>
    public int HoldEndReminderMinutes { get; set; } = 5;

    /// <summary>BR-06: fixed deposit per vehicle type code; types not listed use <see cref="DefaultDeposit"/>.</summary>
    public Dictionary<string, decimal> DepositByVehicleType { get; set; } = new();

    public decimal DefaultDeposit { get; set; } = 30_000;

    /// <summary>BR-01: only these vehicle types may book ("chỉ ô tô" by default).</summary>
    public string[] ReservableVehicleTypeCodes { get; set; } = ["CAR_4", "CAR_7"];
}

/// <summary>XXIV Wallet limits for customers.</summary>
public class WalletRules
{
    /// <summary>Smallest online top-up accepted.</summary>
    public decimal MinTopUp { get; set; } = 10_000;

    /// <summary>
    /// Most a customer can top up online in one calendar day (Vietnam time). Paid and still-open
    /// top-ups of the day both count, so parallel payment links cannot go over it.
    /// </summary>
    public decimal DailyTopUpLimit { get; set; } = 10_000_000;
}

/// <summary>Gate rules: BR-10, BR-11, BR-18, BR-20 and the turn-around timeout.</summary>
public class GateRules
{
    /// <summary>BR-18: below this ANPR confidence (0..1) the Staff confirms the plate first.</summary>
    public decimal AnprConfidenceThreshold { get; set; } = 0.85m;

    /// <summary>No sensor signal this long after the barrier opened: the vehicle turned around and the session is cancelled.</summary>
    public int EntryConfirmTimeoutMinutes { get; set; } = 2;

    /// <summary>FR-PAY-07: minutes to leave after prepaying.</summary>
    public int ExitWindowMinutes { get; set; } = 15;

    /// <summary>BR-10: charge added when the vehicle leaves after its Exit Window.</summary>
    public decimal ExitWindowLateFee { get; set; } = 10_000;

    /// <summary>BR-11: seconds to wait for a dynamic QR callback before the Staff may switch method.</summary>
    public int PendingPaymentCallbackSeconds { get; set; } = 60;

    /// <summary>A gateway payment link is valid this long.</summary>
    public int PaymentLinkMinutes { get; set; } = 15;

    /// <summary>BR-20: overdue after this many hours parked.</summary>
    public int OverdueHours { get; set; } = 24;

    /// <summary>BR-20: abandoned after this many days parked.</summary>
    public int AbandonedDays { get; set; } = 7;
}

/// <summary>A fallback tariff for one vehicle type (block pricing, BR-22).</summary>
public class DefaultTariff
{
    public string VehicleTypeCode { get; set; }

    public int GraceMinutes { get; set; }

    public int BlockMinutes { get; set; } = 60;

    public decimal BlockPrice { get; set; }

    public decimal? MaxDailyFee { get; set; }

    public decimal LostTicketFee { get; set; }
}
