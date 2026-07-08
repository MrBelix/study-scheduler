using System.Globalization;
using System.Text.Json.Serialization;
using StudyScheduler.API.Core.Authentication;

namespace StudyScheduler.API.Features.Notifications;

// --- Incoming Telegram webhook update (only the fields the bot consumes) ---

public sealed record TelegramUpdate(
    [property: JsonPropertyName("callback_query")] TelegramCallbackQuery? CallbackQuery);

public sealed record TelegramCallbackQuery(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("from")] TelegramUser From,
    [property: JsonPropertyName("message")] TelegramCallbackMessage? Message,
    [property: JsonPropertyName("data")] string? Data);

public sealed record TelegramCallbackMessage(
    [property: JsonPropertyName("message_id")] long MessageId,
    [property: JsonPropertyName("chat")] TelegramChat Chat);

public sealed record TelegramChat([property: JsonPropertyName("id")] long Id);

// --- Follow-up button actions and their callback-data encoding ---

public enum FollowUpAction
{
    Completed,
    Paid,
    Cancelled,
}

/// <summary>
/// A lesson slot referenced from callback data: a physical lesson by id, or a (possibly still
/// virtual) series occurrence by series id + original date.
/// </summary>
public readonly record struct LessonSlotRef(Guid? LessonId, Guid? SeriesId, DateOnly? OccurrenceDate);

/// <summary>
/// Encodes a follow-up button press into Telegram callback data and back. The format must stay
/// within Telegram's 64-byte limit: <c>d|p|c :L:{guid:N}</c> (37 chars) or
/// <c>d|p|c :S:{guid:N}:{yyyyMMdd}</c> (46 chars).
/// </summary>
public static class FollowUpCallback
{
    private const string DateFormat = "yyyyMMdd";

    public static string Format(FollowUpAction action, Guid? lessonId, Guid? seriesId, DateOnly? occurrenceDate)
    {
        var prefix = action switch
        {
            FollowUpAction.Completed => "d",
            FollowUpAction.Paid => "p",
            FollowUpAction.Cancelled => "c",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };

        return seriesId is { } series && occurrenceDate is { } date
            ? $"{prefix}:S:{series:N}:{date.ToString(DateFormat, CultureInfo.InvariantCulture)}"
            : $"{prefix}:L:{lessonId!.Value:N}";
    }

    public static bool TryParse(string? data, out FollowUpAction action, out LessonSlotRef slot)
    {
        action = default;
        slot = default;
        if (data is null)
            return false;

        var parts = data.Split(':');
        if (parts.Length < 3)
            return false;

        switch (parts[0])
        {
            case "d": action = FollowUpAction.Completed; break;
            case "p": action = FollowUpAction.Paid; break;
            case "c": action = FollowUpAction.Cancelled; break;
            default: return false;
        }

        switch (parts[1])
        {
            case "L" when parts.Length == 3 && Guid.TryParseExact(parts[2], "N", out var lessonId):
                slot = new LessonSlotRef(lessonId, null, null);
                return true;

            case "S" when parts.Length == 4
                && Guid.TryParseExact(parts[2], "N", out var seriesId)
                && DateOnly.TryParseExact(parts[3], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                slot = new LessonSlotRef(null, seriesId, date);
                return true;

            default:
                return false;
        }
    }
}
