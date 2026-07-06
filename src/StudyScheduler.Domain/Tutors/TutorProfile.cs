namespace StudyScheduler.Domain.Tutors;

/// <summary>
/// Per-tutor settings keyed by the Telegram user id itself (no surrogate id — one profile per
/// tutor, created lazily on first save). Holds what the API cannot derive from initData,
/// currently the tutor's time zone used to generate lesson series occurrences.
/// </summary>
public sealed class TutorProfile
{
    private TutorProfile(long telegramUserId, TimeZoneInfo timeZone, DateTimeOffset createdAtUtc)
    {
        TelegramUserId = telegramUserId;
        TimeZone = timeZone;
        CreatedAtUtc = createdAtUtc;
    }

    public long TelegramUserId { get; private set; }

    /// <summary>The tutor's time zone; persisted by its IANA id (e.g. "Europe/Kyiv").</summary>
    public TimeZoneInfo TimeZone { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static TutorProfile Create(long telegramUserId, TimeZoneInfo timeZone, DateTimeOffset createdAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(telegramUserId);
        ArgumentNullException.ThrowIfNull(timeZone);

        return new TutorProfile(telegramUserId, timeZone, createdAtUtc);
    }

    public void UpdateTimeZone(TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        TimeZone = timeZone;
    }
}
