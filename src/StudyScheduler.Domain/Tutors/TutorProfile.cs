namespace StudyScheduler.Domain.Tutors;

/// <summary>
/// Per-tutor settings keyed by the Telegram user id itself (no surrogate id — one profile per
/// tutor, created lazily on first save). Holds what the API cannot derive from initData: the
/// tutor's time zone used to generate lesson series occurrences and the preferred UI language.
/// </summary>
public sealed class TutorProfile
{
    private TutorProfile(long telegramUserId, TimeZoneInfo timeZone, string? languageCode, DateTimeOffset createdAtUtc)
    {
        TelegramUserId = telegramUserId;
        TimeZone = timeZone;
        LanguageCode = languageCode;
        CreatedAtUtc = createdAtUtc;
    }

    public long TelegramUserId { get; private set; }

    /// <summary>The tutor's time zone; persisted by its IANA id (e.g. "Europe/Kyiv").</summary>
    public TimeZoneInfo TimeZone { get; private set; }

    /// <summary>
    /// Preferred UI language as a lower-case two-letter ISO 639-1 code (e.g. "uk");
    /// <c>null</c> until the tutor explicitly picks one.
    /// </summary>
    public string? LanguageCode { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static TutorProfile Create(
        long telegramUserId,
        TimeZoneInfo timeZone,
        DateTimeOffset createdAtUtc,
        string? languageCode = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(telegramUserId);
        ArgumentNullException.ThrowIfNull(timeZone);

        return new TutorProfile(
            telegramUserId,
            timeZone,
            languageCode is null ? null : NormalizeLanguage(languageCode),
            createdAtUtc);
    }

    public void UpdateTimeZone(TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        TimeZone = timeZone;
    }

    public void UpdateLanguage(string languageCode) => LanguageCode = NormalizeLanguage(languageCode);

    private static string NormalizeLanguage(string languageCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);

        var normalized = languageCode.Trim().ToLowerInvariant();
        if (normalized.Length != 2 || !normalized.All(char.IsAsciiLetterLower))
            throw new ArgumentException(
                "Language code must be a two-letter ISO 639-1 code.", nameof(languageCode));

        return normalized;
    }
}
