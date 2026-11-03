using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Helpers.DemoData;

/// <summary>
/// Demo data for testing every flow by hand (see docs/DEMO_DATA.md): an account for each role, staffed
/// lots with lanes and prices, customers with verified and waiting vehicles, vehicles parked right now
/// (walk-in, on a booking, lost ticket, prepaid, overdue…), upcoming bookings, 30 days of finished visits
/// with their payments, wallets and notifications.
/// Turned on with <c>"Seed:DemoData": true</c> (appsettings.Development.json). Accounts, lot staffing,
/// lanes and prices are added when missing; the rest is written once per database, in one transaction,
/// and an audit log row (<see cref="MarkerAction"/>) records that it ran. Times are relative to the moment
/// it runs, so time-bound cases (an arrival in 10 minutes, a 15-minute exit window) are only fresh then.
/// Rows are built the way the services build them, with the same fee calculator, codes and counters.
/// </summary>
public sealed partial class DemoDataSeed
{
    public const string ConfigKey = "Seed:DemoData";

    /// <summary>Password of the demo customers (and, before 2026-10-29, of every demo account).</summary>
    public const string Password = "Demo@123";

    /// <summary>Password of the demo Managers.</summary>
    public const string ManagerPassword = "Manager123@";

    /// <summary>Password of the demo Staff.</summary>
    public const string StaffPassword = "Staff123@";

    /// <summary>The demo password of an account with this role ("Manager", "Staff", or a customer).</summary>
    public static string PasswordFor(string role) => role switch
    {
        "Manager" => ManagerPassword,
        "Staff" => StaffPassword,
        _ => Password
    };

    public const string MarkerAction = "DemoDataSeeded";

    private const string EmailDomain = "example.com";

    private const string Car4 = "CAR_4";

    private const string Car7 = "CAR_7";

    private const string Motorbike = "MOTORBIKE";

    private readonly PmsDbContext db;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IVehicleDocumentStorage storage;
    private readonly ParkingOptions options;
    private readonly TariffProvider tariffs;
    private readonly ParkingInventory inventory;
    private readonly ILogger logger;
    private readonly int documentRetentionDays;
    private readonly DateTime now;
    private readonly TimeZoneInfo timeZone;

    // Fixed seed: every database gets the same story
    private readonly Random random = new(20261028);

    private readonly Dictionary<string, VehicleType> types = new();
    private readonly Dictionary<string, LotInfo> lots = new();
    private readonly Dictionary<string, ApplicationUser> employees = new();
    private readonly Dictionary<string, CustomerInfo> customers = new();
    private readonly HashSet<int> takenSlots = [];
    private readonly HashSet<string> usedPlates = [];
    private readonly Dictionary<string, List<(DateTime From, DateTime To)>> busyPlates = new();
    private readonly List<WalletOp> walletOps = [];
    private readonly List<ParkingSession> customerSessions = [];
    private readonly List<VehicleVerification> decidedVerifications = [];
    private string adminId;

    private DemoDataSeed(IServiceProvider services)
    {
        db = services.GetRequiredService<PmsDbContext>();
        userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        storage = services.GetRequiredService<IVehicleDocumentStorage>();
        options = services.GetService<ParkingOptions>() ?? new ParkingOptions();
        logger = services.GetService<ILoggerFactory>()?.CreateLogger<DemoDataSeed>();
        documentRetentionDays = services.GetService<VehicleDocumentStorageOptions>()?.RetentionDays ?? 30;
        tariffs = new TariffProvider(db, options);
        inventory = new ParkingInventory(db);
        var clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        now = Trim(clock.GetUtcNow().UtcDateTime);
        timeZone = ParkingText.FindTimeZone(options.TimeZoneId);
    }

    /// <summary>Seeds the demo data when <see cref="ConfigKey"/> is true; does nothing otherwise.</summary>
    public static async Task SeedIfEnabledAsync(IServiceProvider services)
    {
        var configuration = services.GetService<IConfiguration>();
        if (configuration?.GetValue<bool>(ConfigKey) != true)
            return;

        await new DemoDataSeed(services).RunAsync();
    }

    /// <summary>Seeds the demo data (tests call this directly; the API goes through <see cref="SeedIfEnabledAsync"/>).</summary>
    public static Task SeedAsync(IServiceProvider services) => new DemoDataSeed(services).RunAsync();

    private async Task RunAsync()
    {
        foreach (var type in await db.VehicleTypes.Where(t => t.IsActive).ToListAsync())
            types.TryAdd(type.Code, type);
        if (!types.ContainsKey(Car4) || !types.ContainsKey(Car7) || !types.ContainsKey(Motorbike))
        {
            logger?.LogWarning("Demo data skipped: the CAR_4, CAR_7 and MOTORBIKE vehicle types are not all active.");
            return;
        }

        if (!await LoadLotsAsync())
            return;

        await EnsureAccountsAsync();
        await EnsureLotSetupAsync();

        if (await db.AuditLogs.AnyAsync(a => a.Action == MarkerAction))
        {
            logger?.LogInformation("Demo data is already in this database; accounts, staffing, lanes and prices were checked.");
            return;
        }

        var inUse = await PlatesInUseAsync();
        if (inUse.Count > 0)
        {
            logger?.LogWarning("Demo parking data skipped: plates {Plates} are already used in this database.", string.Join(", ", inUse));
            return;
        }

        // One transaction: a failure leaves no half-written story behind (files already saved stay on disk)
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync() : null;

        await SeedPlateListsAsync();
        await SeedCustomerProfilesAsync();
        await SeedCurrentStateAsync();
        await SeedHistoryAsync();
        await SeedWalletsAsync();
        await SeedNotificationsAsync();

        // Vehicles inside each lot: the live counter must match the open sessions that were just written
        await db.SaveChangesAsync();
        foreach (var info in lots.Values)
            info.Lot.OccupiedBays = await db.ParkingSessions.CountAsync(s => s.ParkingLotId == info.Id && SessionStatus.Open.Contains(s.Status));

        db.AuditLogs.Add(new AuditLog
        {
            UserId = adminId,
            UserName = "System",
            Action = MarkerAction,
            EntityName = "DemoData",
            NewValues = $"{{\"sessions\":{await db.ParkingSessions.CountAsync()},\"reservations\":{await db.Reservations.CountAsync()},\"payments\":{await db.Payments.CountAsync()}}}",
            CreatedAt = now
        });
        await db.SaveChangesAsync();

        if (transaction != null)
            await transaction.CommitAsync();

        logger?.LogInformation("Demo data seeded. Passwords: Managers {ManagerPassword}, Staff {StaffPassword}, customers {Password}; see docs/DEMO_DATA.md.", ManagerPassword, StaffPassword, Password);
    }

    /* ---- shared state ------------------------------------------------------------------------- */

    private sealed class LotInfo
    {
        public string Key { get; init; }
        public ParkingLot Lot { get; init; }
        public Gate Entry { get; set; }
        public Gate Exit { get; set; }
        public string ManagerId { get; set; }
        public List<string> StaffIds { get; } = [];
        /// <summary>Staff who worked here before their account was deactivated (older history only).</summary>
        public List<string> FormerStaffIds { get; } = [];
        public List<Zone> Zones { get; set; } = [];

        public int Id => Lot.Id;

        public IReadOnlyList<string> StaffOn(int daysAgo) =>
            daysAgo > 5 && FormerStaffIds.Count > 0 ? [.. StaffIds, .. FormerStaffIds] : StaffIds;
    }

    private sealed class CustomerInfo
    {
        public string Key { get; init; }
        public ApplicationUser User { get; set; }
        public Customer Customer { get; set; }
        /// <summary>Whether past visits may be paid from the wallet (kept off where the balance must stay exact).</summary>
        public bool PaysWithWallet { get; init; }
        public List<(Vehicle Vehicle, DateTime ApprovedAt)> Verified { get; } = [];

        public string UserId => User.Id;
    }

    /// <summary>A wallet movement, written to the ledger in time order once every payment has its id.</summary>
    private sealed record WalletOp(
        CustomerInfo Customer,
        DateTime At,
        decimal Amount,
        string TxnType,
        Payment Payment,
        string ReferenceType,
        string ReferenceId,
        string Description,
        string CreatedBy);

    /* ---- time ----------------------------------------------------------------------------------- */

    private static DateTime Trim(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private DateTime HoursAgo(double hours) => Trim(now.AddHours(-hours));

    private DateTime ToUtc(DateTime local) =>
        Trim(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), timeZone));

    private DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone);

    /* ---- lots, zones and slots ------------------------------------------------------------------- */

    private string FamilyOf(int vehicleTypeId) =>
        ParkingInventory.Family(types.Values.FirstOrDefault(t => t.Id == vehicleTypeId)?.Code);

    /// <summary>Zones of the lot built for this vehicle's family (cars share car floors).</summary>
    private List<Zone> ZonesFor(LotInfo lot, string typeCode) =>
        lot.Zones.Where(z => z.Slots.Count > 0 && FamilyOf(z.Slots.First().VehicleTypeId) == ParkingInventory.Family(typeCode)).ToList();

    /// <summary>The zone a lane would send a walk-in to: the one with the most free spaces.</summary>
    private Zone WalkInZone(LotInfo lot, string typeCode) =>
        ZonesFor(lot, typeCode).OrderByDescending(z => z.ZoneCounter?.AvailableSpaces ?? 0).ThenBy(z => z.Code).First();

    /// <summary>A free bay of the zone not used by another demo case, picked among the first few so the 3D map is not filled in order.</summary>
    private async Task<Slot> TakeFreeSlotAsync(Zone zone)
    {
        var free = await db.Slots
            .Where(s => s.ZoneId == zone.Id && s.IsActive && s.Status == SlotStatus.Available)
            .OrderBy(s => s.Code)
            .ToListAsync();
        free = free.Where(s => !takenSlots.Contains(s.Id)).Take(12).ToList();
        if (free.Count == 0)
            throw new InvalidOperationException($"Demo data: zone {zone.Code} has no free bay left.");

        var slot = free[random.Next(free.Count)];
        takenSlots.Add(slot.Id);
        return slot;
    }

    /* ---- plates ----------------------------------------------------------------------------------- */

    private bool IsBusy(string plate, DateTime from, DateTime to) =>
        busyPlates.TryGetValue(ParkingText.NormalizePlate(plate), out var spans) && spans.Any(s => from < s.To && s.From < to);

    private void MarkBusy(string plate, DateTime from, DateTime to)
    {
        var key = ParkingText.NormalizePlate(plate);
        if (key.Length == 0) return;
        if (!busyPlates.TryGetValue(key, out var spans))
            busyPlates[key] = spans = [];
        spans.Add((from, to));
    }

    /// <summary>A plausible plate nobody else in the demo uses: "51F-123.45" for cars, "59X1-234.56" for motorbikes.</summary>
    private string RandomPlate(string typeCode)
    {
        while (true)
        {
            string plate;
            if (typeCode == Motorbike)
            {
                var province = new[] { "59", "59", "50", "51", "60", "61", "72" }[random.Next(7)];
                var series = "BCDEFGHKLMNPSTUVXYZ"[random.Next(19)];
                plate = $"{province}{series}{random.Next(1, 10)}-{random.Next(100, 1000)}.{random.Next(10, 100)}";
            }
            else
            {
                var province = new[] { "51", "51", "50", "30", "43", "60", "61", "72", "79" }[random.Next(9)];
                var series = "ABCDEFGHKL"[random.Next(10)];
                plate = $"{province}{series}-{random.Next(100, 1000)}.{random.Next(10, 100)}";
            }

            if (usedPlates.Add(ParkingText.NormalizePlate(plate)))
                return plate;
        }
    }

    /* ---- sessions ----------------------------------------------------------------------------------- */

    private decimal AnprConfidence() => decimal.Round(86m + (decimal)random.NextDouble() * 13.5m, 2);

    /// <summary>A session as the entry lane creates it once the loop sensor confirmed the vehicle.</summary>
    private ParkingSession NewSession(LotInfo lot, string plate, string typeCode, DateTime entry, string sessionType,
        CustomerInfo owner, Vehicle vehicle, string operatorId, Zone zone, Slot slot, Tariff tariff, Reservation reservation = null)
    {
        var plateNumber = plate?.Trim().ToUpperInvariant() ?? string.Empty;
        var confirmedAt = entry.AddSeconds(random.Next(12, 45));
        return new ParkingSession
        {
            SessionCode = ParkingText.NewCode("SS", entry),
            ParkingLotId = lot.Id,
            CustomerId = owner?.Customer.Id,
            VehicleId = vehicle?.Id,
            ReservationId = reservation?.Id,
            EntryGateId = lot.Entry.Id,
            EntryTime = entry,
            EntryPlateNumber = plateNumber,
            EntryPlateNormalized = ParkingText.NormalizePlate(plateNumber),
            EntryOperatorUserId = operatorId,
            EntryAnprConfidence = plateNumber.Length > 0 ? AnprConfidence() : null,
            ZoneId = zone?.Id,
            SlotId = slot?.Id,
            PricingRuleId = tariff.RuleId,
            PricingRuleVersion = tariff.Version,
            PriceSnapshotJson = TariffSnapshot.Write(tariff),
            Status = SessionStatus.Active,
            SessionType = sessionType,
            VehicleTypeId = types[typeCode].Id,
            HasPlate = plateNumber.Length > 0,
            EntryConfirmedAt = confirmedAt,
            CreatedAt = entry,
            UpdatedAt = confirmedAt,
            RowVersion = []
        };
    }

    /// <summary>The fee the payment point quotes at <paramref name="at"/> (no free period: the first block is always owed).</summary>
    private FeeBreakdown Quote(Tariff tariff, ParkingSession session, DateTime at) =>
        session.SessionType == SessionType.Whitelist
            ? new FeeBreakdown((int)Math.Ceiling((at - session.EntryTime).TotalMinutes), 0, false, [])
            : FeeCalculator.Calculate(tariff, session.EntryTime, at, timeZone);

    /* ---- payments ----------------------------------------------------------------------------------- */

    private Invoice NewInvoice(int lotId, int? customerId, decimal amount, DateTime at) => new()
    {
        InvoiceNumber = ParkingText.NewCode("INV", at),
        CustomerId = customerId,
        ParkingLotId = lotId,
        SubTotal = amount,
        TotalAmount = amount,
        Status = "Paid",
        DueDate = at,
        PaidAt = at,
        CreatedAt = at
    };

    private string GatewayTransactionNo(string provider) =>
        provider == PaymentProviders.MoMo
            ? $"4{random.NextInt64(100_000_000, 999_999_999)}"
            : $"14{random.Next(100_000, 999_999)}";

    /// <summary>A succeeded payment with its invoice; online ones get a gateway transaction number, wallet ones a ledger line.</summary>
    private Payment NewPayment(int lotId, CustomerInfo customer, int? customerId, decimal amount, DateTime paidAt,
        string method, string provider, string purpose, string channel, string collectedBy)
    {
        var online = method == PaymentMethod.Online;
        var createdAt = online ? paidAt.AddSeconds(-random.Next(40, 150)) : paidAt;
        var invoice = NewInvoice(lotId, customerId, amount, createdAt);
        invoice.PaidAt = paidAt;

        var payment = new Payment
        {
            PaymentCode = ParkingText.NewCode("PM", createdAt),
            Invoice = invoice,
            PaymentMethod = method,
            GatewayProvider = provider,
            Amount = amount,
            Currency = "VND",
            Status = PaymentStatus.Succeeded,
            Purpose = purpose,
            Channel = channel,
            ParkingLotId = lotId,
            CustomerUserId = customer?.UserId,
            CollectedByUserId = collectedBy,
            PaidAt = paidAt,
            CreatedAt = createdAt,
            UpdatedAt = online ? paidAt : null
        };
        if (online)
        {
            payment.ExpiresAt = createdAt.AddMinutes(options.Gate.PaymentLinkMinutes);
            payment.GatewayTransactionNo = GatewayTransactionNo(provider);
        }

        db.Payments.Add(payment);
        return payment;
    }

    /// <summary>A parking fee payment for a saved session (the session's id is needed).</summary>
    private Payment PayFee(ParkingSession session, CustomerInfo customer, decimal amount, DateTime paidAt, string method, string provider, string channel, string collectedBy)
    {
        var payment = NewPayment(session.ParkingLotId, customer, session.CustomerId, amount, paidAt,
            method, method == PaymentMethod.Wallet ? PaymentProviders.Wallet : provider, PaymentPurpose.ParkingFee, channel, collectedBy);
        payment.SessionId = session.Id;
        payment.Invoice.SessionId = session.Id;
        // Cash at the lane does not record the customer; app and gateway payments do
        if (method == PaymentMethod.Cash)
            payment.CustomerUserId = null;

        if (method == PaymentMethod.Wallet)
            walletOps.Add(new WalletOp(customer, paidAt, -amount, "Payment", payment, "ParkingSession", session.SessionCode,
                $"Parking fee for session {session.SessionCode} ({session.EntryPlateNumber})", customer.UserId));
        return payment;
    }

    /// <summary>The booking deposit, paid from the wallet (the reservation's id is needed).</summary>
    private Payment PayDeposit(Reservation reservation, CustomerInfo customer, DateTime paidAt)
    {
        var payment = NewPayment(reservation.ParkingLotId, customer, customer.Customer.Id, reservation.DepositAmount, paidAt,
            PaymentMethod.Wallet, PaymentProviders.Wallet, PaymentPurpose.Deposit, PaymentChannel.App, null);
        payment.ReservationId = reservation.Id;
        payment.Invoice.ReservationId = reservation.Id;
        walletOps.Add(new WalletOp(customer, paidAt, -reservation.DepositAmount, "Payment", payment, "Reservation", reservation.ReservationCode,
            $"Deposit for reservation {reservation.ReservationCode}", customer.UserId));
        return payment;
    }

    /// <summary>A wallet refund of part of a saved payment, as PaymentService.RefundAsync writes it.</summary>
    private void RefundToWallet(Payment original, CustomerInfo customer, decimal amount, DateTime at, string reason, string requestedBy)
    {
        original.RefundedAmount += amount;
        original.UpdatedAt = at;
        var refund = new Payment
        {
            PaymentCode = ParkingText.NewCode("RF", at),
            Invoice = original.Invoice,
            InvoiceId = original.InvoiceId,
            PaymentMethod = original.PaymentMethod,
            GatewayProvider = original.GatewayProvider,
            Amount = amount,
            Currency = "VND",
            Status = PaymentStatus.Refunded,
            Purpose = PaymentPurpose.Refund,
            ReservationId = original.ReservationId,
            SessionId = original.SessionId,
            ParkingLotId = original.ParkingLotId,
            CustomerUserId = original.CustomerUserId,
            Channel = original.Channel,
            RefundOfPaymentId = original.Id,
            FailureReason = reason,
            PaidAt = at,
            CreatedAt = at
        };
        db.Payments.Add(refund);
        walletOps.Add(new WalletOp(customer, at, amount, "Refund", refund, original.Purpose ?? "Refund", original.PaymentCode,
            $"Refund: {reason}", requestedBy ?? "System"));
    }

    /* ---- reservations ------------------------------------------------------------------------------- */

    private decimal DepositFor(string typeCode) =>
        options.Reservations.DepositByVehicleType.TryGetValue(typeCode, out var amount) ? amount : options.Reservations.DefaultDeposit;

    private void LogReservation(Reservation reservation, string action, string oldValue, string newValue, string by, DateTime at) =>
        db.ReservationChangeLogs.Add(new ReservationChangeLog
        {
            ReservationId = reservation.Id,
            Action = action,
            OldValue = oldValue?.Length > 200 ? oldValue[..200] : oldValue,
            NewValue = newValue?.Length > 200 ? newValue[..200] : newValue,
            ChangedBy = by,
            ChangedAt = at
        });

    /// <summary>
    /// A booking as ReservationService.CreateAsync makes it (saved, with its "Created" log line). With a
    /// deposit it is then paid from the wallet a minute or two later and confirmed, unless
    /// <paramref name="payDeposit"/> is false. <paramref name="holdSlot"/> locks the bay (Reserved, one
    /// space off the zone counter) for bookings that are still active.
    /// </summary>
    private async Task<Reservation> BookAsync(CustomerInfo customer, Vehicle vehicle, LotInfo lot, Slot slot, DateTime createdAt,
        DateTime arrival, bool withDeposit, bool holdSlot, string mode = BookingMode.PayAsYouGo, DateTime? departure = null,
        bool payDeposit = true)
    {
        var rules = options.Reservations;
        var typeCode = types.Values.First(t => t.Id == vehicle.VehicleTypeId).Code;
        var heldAt = arrival.AddMinutes(-rules.HoldAheadMinutes) <= createdAt ? createdAt : arrival.AddMinutes(-rules.HoldAheadMinutes);
        var reservation = new Reservation
        {
            ReservationCode = ParkingText.NewCode("RSV", createdAt),
            CustomerId = customer.Customer.Id,
            UserId = customer.UserId,
            VehicleId = vehicle.Id,
            VehicleTypeId = vehicle.VehicleTypeId,
            ParkingLotId = lot.Id,
            ZoneId = slot.ZoneId,
            SlotId = slot.Id,
            StartTime = arrival,
            EndTime = arrival.AddMinutes(rules.HoldMinutesFor(withDeposit)),
            BookingMode = mode,
            DepartureTime = departure,
            SlotHeldAt = heldAt <= now ? heldAt : null,
            DepositAmount = withDeposit ? DepositFor(typeCode) : 0,
            DepositDueAt = withDeposit ? createdAt.AddMinutes(rules.DepositMinutes) : null,
            Status = withDeposit ? ReservationStatus.PendingDeposit : ReservationStatus.Confirmed,
            ConfirmedAt = withDeposit ? null : createdAt,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

        if (holdSlot && reservation.SlotHeldAt != null)
        {
            var tracked = await db.Slots.FirstAsync(s => s.Id == slot.Id);
            await inventory.HoldAsync(tracked, lot.Id);
        }

        db.Reservations.Add(reservation);
        await db.SaveChangesAsync();
        LogReservation(reservation, "Created", null,
            $"{slot.Code} {mode} @ {arrival:O}{(departure != null ? $" - {departure:O}" : "")}{(withDeposit ? "" : " no deposit")}",
            customer.UserId, createdAt);

        if (withDeposit && payDeposit)
        {
            var paidAt = createdAt.AddSeconds(random.Next(40, 240));
            var payment = PayDeposit(reservation, customer, paidAt);
            reservation.Status = ReservationStatus.Confirmed;
            reservation.ConfirmedAt = paidAt;
            reservation.UpdatedAt = paidAt;
            await db.SaveChangesAsync();
            LogReservation(reservation, "Confirmed", ReservationStatus.PendingDeposit, payment.PaymentCode, "System", paidAt);
        }

        await db.SaveChangesAsync();
        return reservation;
    }

    /// <summary>Ends a booking the way ReservationService.EndAsync does (Cancelled, NoShow, Expired, LotCancelled).</summary>
    private void EndReservation(Reservation reservation, string status, string reason, string by, string userId, DateTime at)
    {
        LogReservation(reservation, status, reservation.Status, reason, userId ?? by, at);
        reservation.Status = status;
        reservation.CancelReason = reason;
        reservation.CancelledBy = by;
        reservation.CancelledAt = at;
        reservation.UpdatedAt = at;
    }

    /// <summary>Fixed clock for the notification job, moved to each event's time.</summary>
    private sealed class SettableClock : TimeProvider
    {
        public DateTime UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(UtcNow, DateTimeKind.Utc));
    }
}
