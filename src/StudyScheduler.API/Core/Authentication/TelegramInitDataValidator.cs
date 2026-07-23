using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace StudyScheduler.API.Core.Authentication;

/// <summary>Reason an init data string failed validation.</summary>
public enum TelegramAuthError
{
    None,
    MissingData,
    InvalidSignature,
}

/// <summary>
/// Validates Telegram Mini App init data per
/// https://core.telegram.org/bots/webapps#validating-data-received-via-the-mini-app.
/// The HMAC secret key is derived once from the bot token (it is constant for the
/// process lifetime), so each request only pays for one HMAC over the check string.
/// No <c>auth_date</c> freshness check: initData is fixed at app launch and never
/// refreshes, so any TTL eventually locks out a long-lived WebView session.
/// </summary>
public sealed class TelegramInitDataValidator
{
    private readonly byte[] _secretKey;

    public TelegramInitDataValidator(IOptions<TelegramAuthOptions> options)
    {
        _secretKey = TelegramInitData.DeriveSecretKey(options.Value.BotToken);
    }

    /// <summary>
    /// Verifies the signature of <paramref name="initData"/> and, on success,
    /// returns the embedded user. Returns the specific failure reason otherwise.
    /// </summary>
    public TelegramAuthError Validate(string initData, out TelegramUser? user)
    {
        user = null;

        if (string.IsNullOrWhiteSpace(initData))
            return TelegramAuthError.MissingData;

        var parsed = QueryHelpers.ParseQuery(initData); // values are URL-decoded

        if (!parsed.TryGetValue("hash", out var hashValues) ||
            hashValues.ToString() is not { Length: > 0 } providedHash)
            return TelegramAuthError.MissingData;

        // data-check-string = all received fields except hash (signature included), per spec.
        var dataCheckString = TelegramInitData.BuildDataCheckString(
            parsed.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.ToString())));

        var computed = TelegramInitData.ComputeHash(_secretKey, dataCheckString);

        byte[] providedBytes;
        try
        {
            providedBytes = Convert.FromHexString(providedHash);
        }
        catch (FormatException)
        {
            return TelegramAuthError.InvalidSignature;
        }

        if (!CryptographicOperations.FixedTimeEquals(computed, providedBytes))
            return TelegramAuthError.InvalidSignature;

        if (!parsed.TryGetValue("user", out var userValues) ||
            userValues.ToString() is not { Length: > 0 } userJson)
            return TelegramAuthError.MissingData;

        try
        {
            user = JsonSerializer.Deserialize<TelegramUser>(userJson);
        }
        catch (JsonException)
        {
            return TelegramAuthError.MissingData;
        }

        return user is null ? TelegramAuthError.MissingData : TelegramAuthError.None;
    }
}
