using System.Security.Cryptography;
using System.Text;

namespace StudyScheduler.API.Core.Authentication;

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
    /// Fields sorted by key and joined as <c>key=value</c> with '\n', excluding both <c>hash</c>
    /// and <c>signature</c>. Telegram computes the HMAC <c>hash</c> over the data WITHOUT the
    /// separate Ed25519 <c>signature</c> field, so including it here would make every real
    /// request fail the signature check. <c>signature</c> is only used by third-party validators.
    /// </summary>
    public static string BuildDataCheckString(IEnumerable<KeyValuePair<string, string>> fields) =>
        string.Join('\n', fields
            .Where(kv => kv.Key != "hash" && kv.Key != "signature")
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>HMAC_SHA256(secretKey, dataCheckString) as raw bytes.</summary>
    public static byte[] ComputeHash(byte[] secretKey, string dataCheckString) =>
        HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(dataCheckString));
}
