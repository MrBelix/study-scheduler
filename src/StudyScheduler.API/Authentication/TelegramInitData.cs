using System.Security.Cryptography;
using System.Text;

namespace StudyScheduler.API.Authentication;

/// <summary>
/// Shared primitives for the Telegram init data HMAC scheme — used both to verify incoming
/// data (<see cref="TelegramInitDataValidator"/>) and to mint test data in development.
/// Single source of truth for the algorithm.
/// </summary>
internal static class TelegramInitData
{
    /// <summary>secret_key = HMAC_SHA256(key = "WebAppData", message = bot_token).</summary>
    public static byte[] DeriveSecretKey(string botToken) =>
        HMACSHA256.HashData("WebAppData"u8.ToArray(), Encoding.UTF8.GetBytes(botToken));

    /// <summary>
    /// Fields sorted by key and joined as <c>key=value</c> with '\n', excluding only <c>hash</c>.
    /// Per the Telegram spec the data-check-string is "all received fields except hash", so
    /// <c>signature</c> (the separate Ed25519 third-party field) IS included in the HMAC check.
    /// </summary>
    public static string BuildDataCheckString(IEnumerable<KeyValuePair<string, string>> fields) =>
        string.Join('\n', fields
            .Where(kv => kv.Key != "hash")
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>HMAC_SHA256(secretKey, dataCheckString) as raw bytes.</summary>
    public static byte[] ComputeHash(byte[] secretKey, string dataCheckString) =>
        HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(dataCheckString));
}
