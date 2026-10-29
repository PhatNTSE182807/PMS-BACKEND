namespace PMS.Application.Helpers.Parking;

/// <summary>
/// A pricing rule reduced to what the fee needs (FR-PRC-02/03). Built from a PricingRule row or from a
/// configured default tariff.
/// </summary>
public sealed record Tariff
{
    /// <summary>"Block" (default; "Hourly" is the same with 60-minute blocks), "PerEntry" or "DayNight".</summary>
    public string ChargeModel { get; init; } = "Block";

    /// <summary>BR-22: free when the whole stay is at most this long; longer stays pay from the first minute.</summary>
    public int GraceMinutes { get; init; }

    /// <summary>Charged once per session ("PerEntry": once per calendar day).</summary>
    public decimal EntryFee { get; init; }

    /// <summary>Optional first block with its own length and price (first calendar day only).</summary>
    public int? FirstBlockMinutes { get; init; }

    public decimal? FirstBlockPrice { get; init; }

    public int BlockMinutes { get; init; } = 60;

    public decimal BlockPrice { get; init; }

    /// <summary>Cap per calendar day (BR-22: "trần/ngày tính theo ngày lịch").</summary>
    public decimal? MaxDailyFee { get; init; }

    /// <summary>"DayNight": blocks starting in [DayStart, NightStart) cost DayPrice, the others NightPrice.</summary>
    public TimeOnly? DayStart { get; init; }

    public TimeOnly? NightStart { get; init; }

    public decimal? DayPrice { get; init; }

    public decimal? NightPrice { get; init; }

    public decimal LostTicketFee { get; init; }

    /// <summary>The PricingRule row and version this came from; null for a configured default (BR-22, FR-PRC-04).</summary>
    public int? RuleId { get; init; }

    public int? Version { get; init; }
}

/// <summary>One calendar day of a stay.</summary>
public sealed record FeeDay(DateOnly Date, int Minutes, int Blocks, decimal Amount, bool Capped);

/// <summary>The fee of a stay and how it was reached (FR-PRC-06 shows the blocks).</summary>
public sealed record FeeBreakdown(int DurationMinutes, decimal Total, bool WithinGrace, IReadOnlyList<FeeDay> Days);

/// <summary>What is left to pay, or to give back, when the vehicle leaves (FR-RSV-07, BR-03, BR-10).</summary>
public sealed record Settlement(
    decimal Fee,
    decimal LostTicketFee,
    decimal ExitWindowLateFee,
    decimal DepositApplied,
    decimal AlreadyPaid,
    decimal AmountDue,
    decimal DepositRefund);

/// <summary>
/// Parking fee rules of SRS BR-09, BR-10 and BR-22. Pure functions: every example of the SRS fee table
/// (section 5) is a unit test.
/// </summary>
public static class FeeCalculator
{
    /// <summary>BR-22: fees are rounded up to a multiple of 1,000 VND.</summary>
    public const decimal RoundingStep = 1_000m;

    /// <summary>
    /// The fee for a stay from <paramref name="entryUtc"/> to <paramref name="exitUtc"/>. Days are split at
    /// local midnight in <paramref name="timeZone"/>; blocks are counted per day and rounded up; each day is
    /// capped by <see cref="Tariff.MaxDailyFee"/>.
    /// Team change to BR-09 / BR-22: there is no free period. The first block (the first hour by default)
    /// is owed from the moment the vehicle enters, so a stay is never charged 0; <see cref="Tariff.GraceMinutes"/>
    /// is no longer used here.
    /// </summary>
    public static FeeBreakdown Calculate(Tariff tariff, DateTime entryUtc, DateTime exitUtc, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(tariff);
        timeZone ??= TimeZoneInfo.Utc;

        var duration = exitUtc <= entryUtc ? 0 : (int)Math.Ceiling((exitUtc - entryUtc).TotalSeconds / 60d);

        var entryLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(entryUtc, DateTimeKind.Utc), timeZone);
        var exitLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(exitUtc, DateTimeKind.Utc), timeZone);
        // A vehicle that leaves the minute it entered still owes the first block
        if (exitLocal <= entryLocal)
            exitLocal = entryLocal.AddMinutes(1);

        var days = new List<FeeDay>();
        var dayStart = entryLocal;
        var first = true;
        while (dayStart < exitLocal)
        {
            var nextMidnight = dayStart.Date.AddDays(1);
            var dayEnd = exitLocal < nextMidnight ? exitLocal : nextMidnight;
            var minutes = (int)Math.Ceiling((dayEnd - dayStart).TotalSeconds / 60d);

            var (blocks, amount) = PriceDay(tariff, dayStart, minutes, first);
            var capped = tariff.MaxDailyFee is { } cap && amount > cap;
            if (capped) amount = tariff.MaxDailyFee!.Value;

            days.Add(new FeeDay(DateOnly.FromDateTime(dayStart), minutes, blocks, amount, capped));
            dayStart = dayEnd;
            first = false;
        }

        var total = days.Sum(d => d.Amount);
        if (!string.Equals(tariff.ChargeModel, "PerEntry", StringComparison.OrdinalIgnoreCase))
            total += tariff.EntryFee;

        return new FeeBreakdown(duration, RoundUp(total), false, days);
    }

    /// <summary>
    /// Parking simulation (Admin demo mode): every block of the tariff lasts <paramref name="blockSeconds"/>
    /// instead of its minutes, the block prices stay the same and there is no daily cap. The first block is
    /// owed from entry, as in <see cref="Calculate"/>. "DayNight" prices each block by the local time it
    /// starts at; "PerEntry" charges the entry fee once.
    /// </summary>
    public static FeeBreakdown CalculateSimulated(Tariff tariff, DateTime entryUtc, DateTime exitUtc, TimeZoneInfo timeZone, int blockSeconds)
    {
        ArgumentNullException.ThrowIfNull(tariff);
        timeZone ??= TimeZoneInfo.Utc;
        blockSeconds = Math.Max(1, blockSeconds);

        var seconds = exitUtc <= entryUtc ? 0 : (exitUtc - entryUtc).TotalSeconds;
        var duration = (int)Math.Ceiling(seconds / 60d);
        var entryLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(entryUtc, DateTimeKind.Utc), timeZone);

        int blocks;
        decimal amount;
        if (string.Equals(tariff.ChargeModel, "PerEntry", StringComparison.OrdinalIgnoreCase))
        {
            (blocks, amount) = (1, tariff.EntryFee);
        }
        else
        {
            blocks = Math.Max(1, (int)Math.Ceiling(seconds / blockSeconds));
            amount = tariff.EntryFee;
            var cursor = entryLocal;
            for (var i = 0; i < blocks; i++)
            {
                amount += i == 0 && tariff.FirstBlockMinutes is > 0 && tariff.FirstBlockPrice is { } firstPrice
                    ? firstPrice
                    : BlockPrice(tariff, cursor);
                cursor = cursor.AddSeconds(blockSeconds);
            }
        }

        var day = new FeeDay(DateOnly.FromDateTime(entryLocal), duration, blocks, amount, false);
        return new FeeBreakdown(duration, RoundUp(amount), false, [day]);
    }

    /// <summary>
    /// Settles a stay at the exit: the fee (plus a lost-ticket fine and the BR-10 late charge when the Exit
    /// Window was missed) minus the deposit and what was paid already. A deposit larger than the fee leaves
    /// a residual that is refunded (BR-03); money paid earlier is never refunded here.
    /// </summary>
    public static Settlement Settle(decimal fee, decimal deposit, decimal alreadyPaid, bool exitWindowMissed, decimal exitWindowLateFee, decimal lostTicketFee = 0)
    {
        var late = exitWindowMissed ? exitWindowLateFee : 0;
        var gross = fee + lostTicketFee + late;
        var credit = deposit + alreadyPaid;
        var due = Math.Max(0, gross - credit);

        // Only the deposit part of an overpayment is a residual (BR-03)
        var depositUsed = Math.Min(deposit, Math.Max(0, gross - alreadyPaid));
        var depositRefund = deposit - depositUsed;

        return new Settlement(fee, lostTicketFee, late, depositUsed, alreadyPaid, RoundUp(due), depositRefund);
    }

    public static decimal RoundUp(decimal amount) =>
        amount <= 0 ? 0 : Math.Ceiling(amount / RoundingStep) * RoundingStep;

    private static (int Blocks, decimal Amount) PriceDay(Tariff tariff, DateTime dayStartLocal, int minutes, bool firstDay)
    {
        if (string.Equals(tariff.ChargeModel, "PerEntry", StringComparison.OrdinalIgnoreCase))
            return (1, tariff.EntryFee);

        var blockMinutes = tariff.BlockMinutes > 0 ? tariff.BlockMinutes : 60;
        var blocks = 0;
        decimal amount = 0;
        var remaining = minutes;
        var cursor = dayStartLocal;

        if (firstDay && tariff.FirstBlockMinutes is > 0 && tariff.FirstBlockPrice is { } firstPrice)
        {
            blocks++;
            amount += firstPrice;
            remaining -= tariff.FirstBlockMinutes.Value;
            cursor = cursor.AddMinutes(tariff.FirstBlockMinutes.Value);
        }

        while (remaining > 0)
        {
            blocks++;
            amount += BlockPrice(tariff, cursor);
            remaining -= blockMinutes;
            cursor = cursor.AddMinutes(blockMinutes);
        }

        return (blocks, amount);
    }

    private static decimal BlockPrice(Tariff tariff, DateTime blockStartLocal)
    {
        if (!string.Equals(tariff.ChargeModel, "DayNight", StringComparison.OrdinalIgnoreCase)
            || tariff.DayStart is not { } dayStart || tariff.NightStart is not { } nightStart)
            return tariff.BlockPrice;

        var time = TimeOnly.FromDateTime(blockStartLocal);
        var isDay = dayStart <= nightStart
            ? time >= dayStart && time < nightStart
            : time >= dayStart || time < nightStart;
        return (isDay ? tariff.DayPrice : tariff.NightPrice) ?? tariff.BlockPrice;
    }
}
