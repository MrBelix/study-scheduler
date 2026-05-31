using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace StudyScheduler.API.Authentication;

/// <summary>Reason an init data string failed validation.</summary>
public enum TelegramAuthError
{
    None,
    MissingData,
    InvalidSignature,
    Expired,
}

/// <summary>
/// Validates Telegram Mini App init data per
/// https://core.telegram.org/bots/webapps#validating-data-received-via-the-mini-app.
/// The HMAC secret key is derived once from the bot token (it is constant for the
/// process lifetime), so each request only pays for one HMAC over the check string.
/// </summary>
public sealed class TelegramInitDataValidator
{
    private readonly TelegramAuthOptions _options;
    private readonly TimeProvider _clock;
    private readonly byte[] _secretKey;

    public TelegramInitDataValidator(IOptions<TelegramAuthOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
        _secretKey = TelegramInitData.DeriveSecretKey(_options.BotToken);
    }

    /// <summary>
    /// Verifies the signature and freshness of <paramref name="initData"/> and, on success,
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

        // auth_date is inside the signed payload, so we only trust it after the HMAC check passes.
        if (!parsed.TryGetValue("auth_date", out var authDateRaw) ||
            !long.TryParse(authDateRaw, out var authUnix))
            return TelegramAuthError.MissingData;

        var authDate = DateTimeOffset.FromUnixTimeSeconds(authUnix);
        if (_clock.GetUtcNow() - authDate > _options.MaxAuthAge)
            return TelegramAuthError.Expired;

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
