using System.Security.Cryptography;
using System.Text;

namespace StudyScheduler.Tests;

/// <summary>
/// Independently mints Telegram init data the way the Telegram servers do, so the validator
/// is exercised as a true black box. The check string excludes only <c>hash</c> — every other
/// received field (including the newer <c>signature</c>) is folded into the HMAC, matching the
/// behaviour verified against real init data.
/// </summary>
internal static class TelegramInitDataFactory
{
    public const string BotToken = "123456:TEST-bot-token";

    private static byte[] SecretKey() =>
        HMACSHA256.HashData("WebAppData"u8.ToArray(), Encoding.UTF8.GetBytes(BotToken));

    public static string Hash(IEnumerable<KeyValuePair<string, string>> fields)
    {
        var checkString = string.Join('\n', fields
            .Where(kv => kv.Key != "hash")
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));

        return Convert.ToHexString(
            HMACSHA256.HashData(SecretKey(), Encoding.UTF8.GetBytes(checkString)));
    }

    /// <summary>Builds a signed init data query string. Pass <paramref name="hashOverride"/> to forge a bad hash.</summary>
    public static string Query(IDictionary<string, string> fields, string? hashOverride = null)
    {
        var all = new List<KeyValuePair<string, string>>(fields)
        {
            new("hash", hashOverride ?? Hash(fields)),
        };
        return Encode(all);
    }

    /// <summary>Builds a query string exactly as given, without adding a hash.</summary>
    public static string RawQuery(IEnumerable<KeyValuePair<string, string>> fields) => Encode(fields);

    private static string Encode(IEnumerable<KeyValuePair<string, string>> fields) =>
        string.Join('&', fields.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
}
