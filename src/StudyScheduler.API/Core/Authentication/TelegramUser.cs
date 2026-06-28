using System.Text.Json.Serialization;

namespace StudyScheduler.API.Core.Authentication;

/// <summary>
/// User payload embedded in the <c>user</c> field of Telegram init data (snake_case JSON).
/// </summary>
public sealed class TelegramUser
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("first_name")]
    public string? FirstName { get; init; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("language_code")]
    public string? LanguageCode { get; init; }

    [JsonPropertyName("is_premium")]
    public bool IsPremium { get; init; }

    [JsonPropertyName("photo_url")]
    public string? PhotoUrl { get; init; }
}
