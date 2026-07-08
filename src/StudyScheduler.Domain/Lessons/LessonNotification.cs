using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.Domain.Lessons;

/// <summary>What a bot notification was about.</summary>
public enum LessonNotificationKind
{
    /// <summary>Reminder sent before the lesson starts.</summary>
    Reminder = 1,

    /// <summary>Follow-up prompt sent after the lesson ends (mark completed/paid/cancelled).</summary>
    FollowUp = 2,
}

/// <summary>
/// Record of a bot notification sent for one lesson slot — the poller's dedup log. A slot is
/// identified by <see cref="SlotKey"/> so virtual (unmaterialized) series occurrences can be
/// tracked without a lesson row; the key stays stable if the slot is materialized later.
/// </summary>
public sealed class LessonNotification : Entity
{
    private LessonNotification(
        Guid id,
        long tutorTelegramId,
        LessonNotificationKind kind,
        string slotKey,
        DateTimeOffset sentAtUtc)
        : base(id)
    {
        TutorTelegramId = tutorTelegramId;
        Kind = kind;
        SlotKey = slotKey;
        SentAtUtc = sentAtUtc;
    }

    public long TutorTelegramId { get; private set; }

    public LessonNotificationKind Kind { get; private set; }

    /// <summary>
    /// Slot identity: series-linked slots use <c>S:{seriesId}:{occurrenceDate}</c> (stable across
    /// materialization), one-off lessons use <c>L:{lessonId}</c>. See <see cref="ForLessonSlot"/>.
    /// </summary>
    public string SlotKey { get; private set; }

    public DateTimeOffset SentAtUtc { get; private set; }

    public static LessonNotification Create(
        long tutorTelegramId,
        LessonNotificationKind kind,
        string slotKey,
        DateTimeOffset sentAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tutorTelegramId);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotKey);
        if (!Enum.IsDefined(kind))
            throw new ArgumentException($"Unknown notification kind '{kind}'.", nameof(kind));

        return new LessonNotification(Guid.NewGuid(), tutorTelegramId, kind, slotKey, sentAtUtc);
    }

    /// <summary>Canonical slot key for a lesson or virtual occurrence (see <see cref="SlotKey"/>).</summary>
    public static string ForLessonSlot(Guid? lessonId, Guid? seriesId, DateOnly? occurrenceDate) =>
        seriesId is { } series && occurrenceDate is { } date
            ? $"S:{series}:{date:yyyy-MM-dd}"
            : $"L:{lessonId ?? throw new ArgumentNullException(nameof(lessonId))}";
}
