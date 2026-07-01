using System.Security.Cryptography;
using System.Text;

namespace StudyScheduler.IntegrationTests;

/// <summary>
/// Mints valid Telegram init data for a fake user, signed with the same test bot token the
/// AppHost injects into the API. Lets integration tests exercise the real auth pipeline.
/// </summary>
internal static class TelegramInitData
{
    // Must match the fallback token in AppHost.cs.
    public const string BotToken = "123456:TEST-bot-token";

    public static string ForUser(long userId, string firstName)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            ["user"] = $"{{\"id\":{userId},\"first_name\":\"{firstName}\"}}",
        };

        var checkString = string.Join('\n', fields.Select(kv => $"{kv.Key}={kv.Value}"));
        var secret = HMACSHA256.HashData("WebAppData"u8.ToArray(), Encoding.UTF8.GetBytes(BotToken));
        var hash = Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(checkString)));

        var all = fields.Append(new KeyValuePair<string, string>("hash", hash));
        return string.Join('&', all.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }
}
