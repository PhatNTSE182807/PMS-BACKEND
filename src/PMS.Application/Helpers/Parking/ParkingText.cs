using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PMS.Application.Helpers.Parking;

/// <summary>Small text helpers shared by reservations, gates and payments.</summary>
public static class ParkingText
{
    /// <summary>Same normalisation as the customer's vehicles (UserProfileService): letters and digits, upper case.</summary>
    public static string NormalizePlate(string plate) =>
        string.IsNullOrWhiteSpace(plate) ? string.Empty : Regex.Replace(plate, "[^a-zA-Z0-9]", string.Empty).ToUpperInvariant();

    /// <summary>
    /// A readable unique code such as RSV-261020-7KQ2M9 (prefix, local date, 6 random characters without
    /// look-alikes). Gateways need it alphanumeric-ish and unique per day (vnp_TxnRef, MoMo orderId).
    /// </summary>
    public static string NewCode(string prefix, DateTime utcNow)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> random = stackalloc char[6];
        for (var i = 0; i < random.Length; i++)
            random[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return $"{prefix}{utcNow:yyMMddHHmmss}{new string(random)}";
    }

    /// <summary>The lot's time zone (Windows or IANA id), falling back to Vietnam time.</summary>
    public static TimeZoneInfo FindTimeZone(string id) => FirstTimeZone(id, "SE Asia Standard Time", "Asia/Ho_Chi_Minh");

    /// <summary>The first id this server knows; a fixed UTC+7 zone when it knows none of them.</summary>
    internal static TimeZoneInfo FirstTimeZone(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(candidate);
            }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // try the next id
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("ICT", TimeSpan.FromHours(7), "Indochina Time", "Indochina Time");
    }
}
