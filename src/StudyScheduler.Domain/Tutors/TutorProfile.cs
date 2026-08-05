using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.Domain.Tutors;

/// <summary>
/// Per-tutor settings keyed by the Telegram user id itself (no surrogate id — one profile per
/// tutor, created lazily on first save). Holds what the API cannot derive from initData: the
/// tutor's time zone used to generate lesson series occurrences and the preferred UI language.
/// </summary>
public sealed class TutorProfile
{
    public const int MinRemindMinutes = 5;
    public const int MaxRemindMinutes = 240;
    public const int DefaultRemindMinutes = 30;

    private TutorProfile(long telegramUserId, TimeZoneInfo timeZone, AppLanguage? languageCode, DateTimeOffset createdAtUtc)
    {
        TelegramUserId = telegramUserId;
        TimeZone = timeZone;
        LanguageCode = languageCode;
        CreatedAtUtc = createdAtUtc;
        RemindMinutes = DefaultRemindMinutes;
        DaySummary = true;
        MorningAgenda = false;
        MorningAgendaAtLocal = new TimeOnly(8, 0);
        BotReachable = true;
    }

    public long TelegramUserId { get; private set; }

    /// <summary>The tutor's time zone; persisted by its IANA id (e.g. "Europe/Kyiv").</summary>
    public TimeZoneInfo TimeZone { get; private set; }

    /// <summary>
    /// Preferred UI language; <c>null</c> until the tutor explicitly picks one. Persisted as its
    /// two-letter ISO 639-1 code ("uk"/"en") via an EF value conversion.
    /// </summary>
    public AppLanguage? LanguageCode { get; private set; }

    /// <summary>
    /// Bot reminder lead time before a lesson, in minutes; <c>null</c> disables reminders.
    /// On by default — the bot can always message a Mini App user.
    /// </summary>
    public int? RemindMinutes { get; private set; }

    /// <summary>Send the evening summary that collects the day's unmarked lessons.</summary>
    public bool DaySummary { get; private set; }

    /// <summary>Send the morning agenda listing the whole day ahead.</summary>
    public bool MorningAgenda { get; private set; }

    /// <summary>Wall-clock time the morning agenda goes out, in the tutor's own zone.</summary>
    public TimeOnly MorningAgendaAtLocal { get; private set; }

    /// <summary>
    /// Optimistic reachability flag: <c>true</c> means the bot's chat with this tutor is assumed
    /// deliverable. Flipped to <c>false</c> when a send is rejected with Telegram 403 (bot not
    /// started or blocked), which stops the poller from targeting this tutor until the bot is
    /// re-enabled (handled later by the /start webhook via <see cref="MarkBotReachable"/>).
    /// </summary>
    public bool BotReachable { get; private set; }

    /// <summary>
    /// Whether the tutor opted into ANY bot notification at all — the single predicate the poller
    /// selects notifiable profiles by, so a new opt-in cannot be forgotten in one of the two places.
    /// </summary>
    public bool WantsAnyNotification => RemindMinutes is not null || DaySummary || MorningAgenda;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static Result<TutorProfile> Create(
        long telegramUserId,
        TimeZoneInfo timeZone,
        DateTimeOffset createdAtUtc,
        AppLanguage? languageCode = null)
    {
        // Programmer errors, not user input: the id comes from validated auth data and the
        // time zone is resolved by the caller before it gets here.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(telegramUserId);
        ArgumentNullException.ThrowIfNull(timeZone);

        // The language is already a validated enum, so there is nothing left to reject here.
        return Result<TutorProfile>.Success(new TutorProfile(
            telegramUserId,
            timeZone,
            languageCode,
            createdAtUtc));
    }

    public void UpdateTimeZone(TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        TimeZone = timeZone;
    }

    /// <summary>Type-safe language setter — the enum can't be invalid, so there is nothing to fail.</summary>
    public void UpdateLanguage(AppLanguage languageCode) => LanguageCode = languageCode;

    /// <summary>Sets the reminder lead time; <c>null</c> turns reminders off.</summary>
    public Result UpdateRemindMinutes(int? remindMinutes)
    {
        if (remindMinutes is { } minutes && minutes is < MinRemindMinutes or > MaxRemindMinutes)
            return Result.Failure(new Error(
                "Profile.RemindMinutesOutOfRange",
                $"Reminder lead time must be between {MinRemindMinutes} and {MaxRemindMinutes} minutes.",
                "RemindMinutes"));

        RemindMinutes = remindMinutes;
        return Result.Success();
    }

    public void UpdateDaySummary(bool enabled) => DaySummary = enabled;

    public void UpdateMorningAgenda(bool enabled) => MorningAgenda = enabled;

    /// <summary>Sets when the morning agenda goes out. Every wall-clock time is legal.</summary>
    public void UpdateMorningAgendaAt(TimeOnly localTime) => MorningAgendaAtLocal = localTime;

    /// <summary>Marks the bot chat undeliverable after a 403 so the poller skips this tutor.</summary>
    public void MarkBotUnreachable() => BotReachable = false;

    /// <summary>Restores deliverability (the /start webhook will call this in a later stage).</summary>
    public void MarkBotReachable() => BotReachable = true;
}
