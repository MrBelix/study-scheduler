using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.Domain.Notifications;

/// <summary>
/// What a dispatch's message was LAST rendered from — the only record of the state the tutor is
/// currently looking at, and therefore the only thing a re-render can spot a difference against.
/// Three lines of the text contract are impossible without it: "було {oldStart}" (the reminder's
/// original slot), "оплата лишається · {price} ₴" (the payment flag as it was before
/// <see cref="Lesson.ChangeStatus"/> cleared it on cancel) and "оновлено 14:20 · +1 урок,
/// 1 скасовано" (the agenda's baseline).
/// Plain records on purpose: how it is stored (jsonb) is persistence's business, not the domain's.
/// </summary>
public sealed record RenderedSnapshot(
    string Lang,
    NotificationKind Kind,
    ReminderSnapshot? Reminder,
    DaySnapshot? Day)
{
    /// <summary>
    /// The snapshot of a row nothing was rendered from — the migration's seeded reminders, whose
    /// message predates snapshots entirely.
    /// </summary>
    public static readonly RenderedSnapshot Empty = new("uk", NotificationKind.Reminder, null, null);
}

/// <summary>
/// One reminder as it was last rendered. <paramref name="OriginalStartUtc"/> is stamped on the FIRST
/// send and copied verbatim on every re-render — it is what "було {oldStart}" names.
/// <paramref name="RemindMinutes"/> is frozen at the first send too: the first line quotes the lead
/// time the reminder was armed with, never a live countdown, or every tick would produce a new edit.
/// <paramref name="IsPaid"/> is the LAST-RENDERED payment flag, which is how a cancelled lesson can
/// still say "оплата лишається" after the domain cleared the real one.
/// </summary>
public sealed record ReminderSnapshot(
    Guid LessonId,
    DateTimeOffset OriginalStartUtc,
    DateTimeOffset StartUtc,
    int DurationMinutes,
    int RemindMinutes,
    string StudentName,
    string? Topic,
    decimal Price,
    bool IsPaid,
    LessonStatus Status,
    string Phase);

/// <summary>
/// One day digest (agenda or summary) as it was last rendered. The baselines are stamped on the
/// FIRST send and copied verbatim afterwards, so a re-render can say "+1 урок, 1 скасовано" and
/// "було {oldStart}" per line. <paramref name="UpdatedAtUtc"/> is the instant the agenda's delta
/// against the baseline last actually CHANGED — frozen and copied forward exactly like the
/// baselines themselves whenever the delta is unchanged, so "оновлено 14:20" does not become a
/// live clock that repaints the message (and burns the Telegram budget) every poll tick.
/// </summary>
public sealed record DaySnapshot(
    DateOnly LocalDate,
    IReadOnlyList<DayLineSnapshot> Lines,
    IReadOnlyList<Guid> BaselineLessonIds,
    IReadOnlyDictionary<Guid, DateTimeOffset> BaselineStarts,
    DateTimeOffset? UpdatedAtUtc = null);

/// <summary>One lesson line of a day digest as it was last rendered.</summary>
public sealed record DayLineSnapshot(
    Guid LessonId,
    DateTimeOffset StartUtc,
    string StudentName,
    LessonStatus Status,
    bool IsPaid,
    decimal Price);
