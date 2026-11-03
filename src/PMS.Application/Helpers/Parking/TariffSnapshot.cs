using System.Text.Json;

namespace PMS.Application.Helpers.Parking;

/// <summary>
/// The tariff a booking or a session priced itself with, kept as JSON on the row. The fee is worked out from
/// this copy, so a later change of the lot's price cannot reach a stay in progress or a booking already made.
/// </summary>
public static class TariffSnapshot
{
    public static string Write(Tariff tariff) => JsonSerializer.Serialize(tariff);

    /// <summary>The saved tariff, or null when there is none (older rows) or it cannot be read: the caller then prices from the live rule.</summary>
    public static Tariff Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Tariff>(json);
        }
        catch (JsonException)
        {
            // A damaged copy must never stop a vehicle from leaving
            return null;
        }
    }
}
