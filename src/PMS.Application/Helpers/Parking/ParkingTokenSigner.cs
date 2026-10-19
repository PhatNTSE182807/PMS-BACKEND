using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PMS.Application.Models.Operations;

namespace PMS.Application.Helpers.Parking;

/// <summary>
/// Signs and checks the self-contained codes of NFR-16: the reservation QR, the entry ticket (lot code,
/// entry time, lane, FR-OPS-09) and the Exit Pass (FR-PAY-07). Format: <c>kind.payload.signature</c>,
/// base64url, HMAC-SHA256 with a per-lot key derived from <see cref="ParkingOptions.TicketSigningKey"/>,
/// so an Edge that holds one lot's key cannot forge another lot's codes.
/// </summary>
public class ParkingTokenSigner
{
    public const string ReservationKind = "rsv";
    public const string TicketKind = "tkt";
    public const string ExitPassKind = "exit";

    private readonly byte[] _masterKey;

    public ParkingTokenSigner(ParkingOptions options, ILogger<ParkingTokenSigner> logger = null)
    {
        if (string.IsNullOrWhiteSpace(options?.TicketSigningKey))
        {
            logger?.LogWarning("Parking:TicketSigningKey is not set; QR codes and tickets use a random key and stop working after a restart.");
            _masterKey = RandomNumberGenerator.GetBytes(32);
        }
        else
        {
            _masterKey = Encoding.UTF8.GetBytes(options.TicketSigningKey);
        }
    }

    /// <summary>Signs <paramref name="claims"/> (string values) for <paramref name="lotId"/>.</summary>
    public string Sign(string kind, int lotId, IDictionary<string, string> claims)
    {
        var body = new Dictionary<string, string>(claims) { ["lot"] = lotId.ToString() };
        var payload = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body)));
        var signature = Base64Url(Hmac(lotId, $"{kind}.{payload}"));
        return $"{kind}.{payload}.{signature}";
    }

    /// <summary>
    /// Checks the signature and the kind; returns the claims (with "lot") or null when the token is not
    /// genuine. Expiry is the caller's business since each kind has its own window.
    /// </summary>
    public IReadOnlyDictionary<string, string> Verify(string token, string expectedKind)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var parts = token.Trim().Split('.');
        if (parts.Length != 3 || parts[0] != expectedKind)
            return null;

        try
        {
            var body = JsonSerializer.Deserialize<Dictionary<string, string>>(FromBase64Url(parts[1]));
            if (body == null || !body.TryGetValue("lot", out var lot) || !int.TryParse(lot, out var lotId))
                return null;

            var expected = Hmac(lotId, $"{parts[0]}.{parts[1]}");
            return CryptographicOperations.FixedTimeEquals(expected, FromBase64Url(parts[2])) ? body : null;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    private byte[] Hmac(int lotId, string data)
    {
        // Per-lot key = HMAC(master, "lot:{id}")
        var lotKey = HMACSHA256.HashData(_masterKey, Encoding.UTF8.GetBytes($"lot:{lotId}"));
        return HMACSHA256.HashData(lotKey, Encoding.UTF8.GetBytes(data));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
