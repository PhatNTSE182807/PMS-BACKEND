using Microsoft.EntityFrameworkCore;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Helpers.Parking;

/// <summary>
/// The colour the 3D scenes (gate cameras and the lot map) paint a vehicle, so it looks the same at the
/// entry lane, in its bay and at the exit lane. A registered vehicle wears its registered colour; any other
/// vehicle gets a colour picked from its plate (or session code when it has no plate), the same every time.
/// The plate itself is never sent with it.
/// </summary>
public static class VehiclePaint
{
    /// <summary>Colours given to unregistered vehicles; the frontend recognises every one of these names.</summary>
    private static readonly string[] Picked = ["White", "Black", "Silver", "Grey", "Red", "Blue", "Dark Blue", "Green", "Yellow", "Orange", "Brown", "Beige"];

    /// <summary>The registered colour when there is one, otherwise a colour picked from the key; null with neither.</summary>
    public static string Of(string registeredColor, string key)
    {
        if (!string.IsNullOrWhiteSpace(registeredColor)) return registeredColor.Trim();
        if (string.IsNullOrWhiteSpace(key)) return null;
        // FNV-1a: stable across processes and restarts, unlike string.GetHashCode
        var hash = 2166136261u;
        foreach (var ch in key) hash = (hash ^ ch) * 16777619u;
        return Picked[hash % (uint)Picked.Length];
    }

    /// <summary>What a session's colour is picked from when the vehicle is not registered.</summary>
    public static string KeyOf(ParkingSession session) =>
        session.HasPlate && !string.IsNullOrEmpty(session.EntryPlateNormalized) ? session.EntryPlateNormalized : session.SessionCode;

    /// <summary>
    /// Registered colours of the given vehicles, and of active vehicles registered with the given plates
    /// (a vehicle that entered without being linked to its account still wears its colour).
    /// </summary>
    public static async Task<(Dictionary<Guid, string> ById, Dictionary<string, string> ByPlate)> RegisteredAsync(
        PmsDbContext context, IEnumerable<Guid> vehicleIds, IEnumerable<string> plates)
    {
        var ids = vehicleIds.Distinct().ToList();
        var plateList = plates.Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
        var byId = ids.Count == 0
            ? new Dictionary<Guid, string>()
            : await context.Vehicles.AsNoTracking()
                .Where(v => ids.Contains(v.Id) && v.Color != null && v.Color != "")
                .ToDictionaryAsync(v => v.Id, v => v.Color);
        var byPlate = plateList.Count == 0
            ? new Dictionary<string, string>()
            : (await context.Vehicles.AsNoTracking()
                    .Where(v => v.IsActive && plateList.Contains(v.PlateNormalized) && v.Color != null && v.Color != "")
                    .Select(v => new { v.PlateNormalized, v.Color, v.Id })
                    .ToListAsync())
                // Several accounts may list the same plate: take one, the same one every time
                .GroupBy(v => v.PlateNormalized)
                .ToDictionary(g => g.Key, g => g.OrderBy(v => v.Id).First().Color);
        return (byId, byPlate);
    }

    /// <summary>The paint of a session: linked vehicle's colour, else a vehicle registered with its plate, else picked.</summary>
    public static string ForSession(ParkingSession session, (Dictionary<Guid, string> ById, Dictionary<string, string> ByPlate) registered)
    {
        var color = session.VehicleId is { } id ? registered.ById.GetValueOrDefault(id) : null;
        if (color == null && session.HasPlate && !string.IsNullOrEmpty(session.EntryPlateNormalized))
            color = registered.ByPlate.GetValueOrDefault(session.EntryPlateNormalized);
        return Of(color, KeyOf(session));
    }
}
