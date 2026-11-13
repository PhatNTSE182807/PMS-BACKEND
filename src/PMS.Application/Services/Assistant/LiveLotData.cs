using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PMS.Application.Models.ParkingLot;
using PMS.Application.Models.Pricing;
using PMS.Application.Services.Impl;

namespace PMS.Application.Services.Assistant;

/// <summary>A snapshot of the public lot data the assistant may quote, and a short version tag of its content.</summary>
public sealed record LiveLotSnapshot(string Text, string Version, int Lots)
{
    public static readonly LiveLotSnapshot Empty = new("", "none", 0);
}

/// <summary>
/// The lots customers can see (open or under maintenance) with their address, opening hours and the prices the
/// gates charge right now, written as plain text for the assistant's prompt. It reuses the public services behind
/// the homepage and the lot price list, so it never shows anything a Guest cannot already see (no plates, no
/// account data). Free bays are left out on purpose: they change every minute and the lot list shows them live.
/// Rebuilt as soon as a lot or a price is saved (<see cref="ILotDataChangeListener"/>), and at least once a minute
/// for changes made another way (a lot going into maintenance when its last Staff is locked, for example).
/// </summary>
public sealed class LiveLotData(IServiceScopeFactory scopes, TimeProvider clock, ILogger<LiveLotData> logger)
    : ILotDataChangeListener
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    /// <summary>Lots beyond this are left out so the prompt stays small.</summary>
    private const int MaxLots = 40;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private LiveLotSnapshot _snapshot = LiveLotSnapshot.Empty;
    private DateTimeOffset _builtAt = DateTimeOffset.MinValue;

    /// <summary>Goes up on every change; a snapshot read before the latest change is not used.</summary>
    private long _changes;
    private long _builtFromChanges = -1;

    private bool IsFresh() =>
        _builtFromChanges == Interlocked.Read(ref _changes) && clock.GetUtcNow() - _builtAt < CacheFor;

    /// <summary>A lot or one of its prices was saved: the next question reads the lots again.</summary>
    public void LotDataChanged(int lotId) => Interlocked.Increment(ref _changes);

    public async Task<LiveLotSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        if (IsFresh())
            return _snapshot;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsFresh())
                return _snapshot;
            // Read before building: a change saved while the lots are being read makes the next question read them again
            var changes = Interlocked.Read(ref _changes);
            try
            {
                _snapshot = await BuildAsync();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The assistant still answers from the help files; the previous snapshot (if any) is kept
                logger.LogWarning(e, "Assistant: the live lot data could not be read");
            }
            _builtAt = clock.GetUtcNow();
            _builtFromChanges = changes;
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LiveLotSnapshot> BuildAsync()
    {
        using var scope = scopes.CreateScope();
        var lotService = scope.ServiceProvider.GetRequiredService<IParkingLotService>();
        var pricing = scope.ServiceProvider.GetRequiredService<IPricingRuleService>();

        var lots = (await lotService.GetPublicLotsAsync()).Take(MaxLots).ToList();
        if (lots.Count == 0)
            return new LiveLotSnapshot("(No parking lot is open to customers right now.)", "no-lots", 0);

        var text = new StringBuilder();
        foreach (var lot in lots)
        {
            text.Append("- ").Append(lot.Name);
            if (!string.IsNullOrWhiteSpace(lot.Code))
                text.Append(" (").Append(lot.Code).Append(')');
            text.Append('\n');
            text.Append("  Address: ").Append(Address(lot)).Append('\n');
            text.Append("  Status: ").Append(lot.IsBookable ? "open, takes bookings" : "under maintenance: can be viewed, takes no bookings").Append('\n');
            text.Append("  Opening hours: ").Append(Hours(lot)).Append('\n');
            if (!string.IsNullOrWhiteSpace(lot.Phone))
                text.Append("  Phone: ").Append(lot.Phone).Append('\n');

            AppendPrices(text, lot, await pricing.GetPublicPricesAsync(lot.Id));
        }

        var body = text.ToString().TrimEnd();
        // The version changes whenever a lot, an address or a price changes, so cached answers never quote old data
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..12];
        return new LiveLotSnapshot(body, version, lots.Count);
    }

    /// <summary>
    /// The lot's bays in total per vehicle family, then the price of each vehicle type it has bays for, so the
    /// assistant never quotes a car price at a motorbike-only lot. A lot whose bays are not set up yet lists the
    /// price of every type. 4- and 7-seat cars share the car bays, so both are listed when the lot has car bays.
    /// </summary>
    public static void AppendPrices(StringBuilder text, PublicParkingLotResponseModel lot, IReadOnlyList<PublicLotPriceModel> prices)
    {
        var bays = (lot.BaysByVehicle ?? [])
            .Where(b => !string.IsNullOrWhiteSpace(b.VehicleType) && b.TotalBays > 0)
            .ToList();
        var families = bays.Select(b => ParkingInventory.Family(b.VehicleType)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shown = bays.Count == 0
            ? prices.ToList()
            : prices.Where(p => families.Contains(ParkingInventory.Family(p.VehicleTypeCode))).ToList();

        if (bays.Count > 0)
            text.Append("  Bays in total: ")
                .Append(string.Join(", ", bays.Select(b => $"{b.TotalBays} {BayName(b.VehicleType)}")))
                .Append('\n');

        if (shown.Count == 0)
        {
            text.Append("  Prices now: no price is set for this lot yet\n");
            return;
        }

        text.Append(bays.Count == 0 ? "  Prices now (bays not set up yet):\n" : "  Prices now:\n");
        foreach (var price in shown)
            text.Append("    ").Append(price.VehicleTypeName).Append(": ").Append(Describe(price)).Append('\n');
    }

    private static string BayName(string family) =>
        string.Equals(family, "MOTORBIKE", StringComparison.OrdinalIgnoreCase) ? "motorbike"
        : string.Equals(family, "CAR", StringComparison.OrdinalIgnoreCase) ? "car"
        : family.ToLowerInvariant();

    /// <summary>The street address, with the district and city added only when the address does not already name them.</summary>
    private static string Address(PublicParkingLotResponseModel lot)
    {
        var address = lot.Address?.Trim() ?? "";
        foreach (var part in new[] { lot.District, lot.City })
        {
            if (string.IsNullOrWhiteSpace(part) || address.Contains(part.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            address = address.Length > 0 ? $"{address}, {part.Trim()}" : part.Trim();
        }
        return address.Length > 0 ? address : "not set";
    }

    private static string Hours(PublicParkingLotResponseModel lot) =>
        lot.Is24Hours || lot.OpenTime == null || lot.CloseTime == null
            ? "24 hours"
            : $"{lot.OpenTime:HH\\:mm} to {lot.CloseTime:HH\\:mm}";

    /// <summary>One line per vehicle type, in the words the help files use (blocks, daily cap).</summary>
    private static string Describe(PublicLotPriceModel price)
    {
        var parts = new List<string>();
        switch (price.ChargeModel)
        {
            case "PerEntry":
                parts.Add($"{Money(price.EntryFee)} per entry, charged once per calendar day");
                break;
            case "DayNight":
                parts.Add($"{Money(price.DayPrice ?? price.BlockPrice)} per {Minutes(price.BlockMinutes)} block by day " +
                          $"({price.DayStartTime:HH\\:mm} to {price.NightStartTime:HH\\:mm}), " +
                          $"{Money(price.NightPrice ?? price.BlockPrice)} per block by night");
                break;
            default:
                if (price.FirstBlockMinutes is { } firstMinutes && price.FirstBlockPrice is { } firstPrice
                    && (firstMinutes != price.BlockMinutes || firstPrice != price.BlockPrice))
                    parts.Add($"first {Minutes(firstMinutes)} {Money(firstPrice)}, then {Money(price.BlockPrice)} per {Minutes(price.BlockMinutes)}");
                else
                    parts.Add($"{Money(price.BlockPrice)} per {Minutes(price.BlockMinutes)}");
                if (price.EntryFee > 0)
                    parts.Add($"plus a {Money(price.EntryFee)} entry fee");
                break;
        }
        if (price.MaxDailyFee is > 0)
            parts.Add($"daily cap {Money(price.MaxDailyFee.Value)} per calendar day");
        return string.Join("; ", parts);
    }

    private static string Money(decimal value) => $"{value.ToString("N0", CultureInfo.InvariantCulture)} VND";

    private static string Minutes(int minutes) =>
        minutes % 60 == 0 ? (minutes == 60 ? "hour" : $"{minutes / 60} hours") : $"{minutes} minutes";
}
