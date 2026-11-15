using System;

namespace PMS.Application.Helpers.Parking;

/// <summary>
/// The one place that turns a zone's bays into a fill level: the live label customers see (Available, AlmostFull,
/// Full) and the alert level that tells the lot's Staff and Manager when a zone is almost full, full, or has
/// space again. Every caller passes the zone counter's total and free bays.
/// <para>
/// Penalty bays (a separate bay kind, being added on its own branch) are not part of a zone's capacity. They must
/// be left out of <c>total</c> / <c>free</c> before they reach this helper; if the counters ever start to include
/// them, subtract them in <see cref="OccupiedPercent"/> and every level below follows.
/// </para>
/// </summary>
public static class ZoneFillLevel
{
    /// <summary>Plenty of free bays.</summary>
    public const string Available = "Available";

    /// <summary>At least <see cref="AlmostFullPercent"/> of the bays are taken.</summary>
    public const string AlmostFull = "AlmostFull";

    /// <summary>No bay is free: walk-ins are turned away at the entry lane.</summary>
    public const string Full = "Full";

    /// <summary>Share of taken bays from which a zone counts as almost full.</summary>
    public const int AlmostFullPercent = 80;

    /// <summary>An almost-full alert is sent again only after the zone dropped below this share (no repeat alerts).</summary>
    public const int RearmPercent = 70;

    /// <summary>Taken bays as a percentage of the zone's bays (0 for a zone with no bay).</summary>
    public static decimal OccupiedPercent(int total, int free)
    {
        if (total <= 0) return 0;
        var taken = Math.Clamp(total - Math.Max(free, 0), 0, total);
        return Math.Round(taken * 100m / total, 1);
    }

    /// <summary>The live label of a zone, a vehicle family or a whole lot, without any memory of earlier alerts.</summary>
    public static string Live(int total, int free) =>
        total <= 0 ? Available
        : free <= 0 ? Full
        : OccupiedPercent(total, free) >= AlmostFullPercent ? AlmostFull
        : Available;

    /// <summary>
    /// Free bays a full zone needs before its "space available again" alert goes out: 5 % of the zone, at least two,
    /// so one vehicle leaving and the next walking in does not send a pair of alerts every minute.
    /// </summary>
    public static int ReopenFreeBays(int total) =>
        Math.Min(Math.Max(total, 1), Math.Max(2, (int)Math.Ceiling(total * 0.05)));

    /// <summary>
    /// The alert level after a counter change, starting from the level last stored on the zone. A zone becomes
    /// AlmostFull at 80 % and Full when no bay is free; a Full zone stays Full until <see cref="ReopenFreeBays"/> bays
    /// are free, and an AlmostFull zone goes back to Available only below 70 %.
    /// </summary>
    public static string NextAlertLevel(string current, int total, int free)
    {
        current = Normalize(current);
        if (total <= 0) return Available;
        if (free <= 0) return Full;

        var percent = OccupiedPercent(total, free);
        if (current == Full && free < ReopenFreeBays(total)) return Full;
        if (percent >= AlmostFullPercent) return AlmostFull;
        if (current is AlmostFull or Full && percent >= RearmPercent) return AlmostFull;
        return Available;
    }

    /// <summary>
    /// The notification a level change sends (a NotificationTypes value), or null when it is silent: reaching
    /// AlmostFull from Available, reaching Full, and leaving Full. Going from AlmostFull back to Available only
    /// re-arms the alert.
    /// </summary>
    public static string NoticeFor(string previous, string next)
    {
        previous = Normalize(previous);
        next = Normalize(next);
        if (previous == next) return null;
        if (next == Full) return Models.Notifications.NotificationTypes.LotFull;
        if (previous == Full) return Models.Notifications.NotificationTypes.LotSpaceAvailable;
        if (next == AlmostFull) return Models.Notifications.NotificationTypes.LotAlmostFull;
        return null;
    }

    /// <summary>Rows saved before the alert level existed read as Available.</summary>
    public static string Normalize(string level) => level is AlmostFull or Full ? level : Available;
}
