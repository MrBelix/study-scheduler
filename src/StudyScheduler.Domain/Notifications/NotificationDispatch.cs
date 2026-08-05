using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.Domain.Notifications;

/// <summary>
/// One bot message that has actually gone out, and everything needed to keep it truthful: the chat
/// and message it occupies, the snapshot it was rendered from, and the hash of the exact text and
/// buttons that reached Telegram (so a re-render that changes nothing costs no API call). A dispatch
/// targets EITHER one lesson (a reminder) OR one local date (an agenda or a summary) — never both,
/// never neither.
/// It deliberately carries no foreign key to the lesson: a reminder must survive its lesson being
/// deleted, because "🔔 Урок видалено" is exactly what the tutor still has to be told.
/// </summary>
public sealed class NotificationDispatch : Entity, ITutorOwned
{
    // EF materialization only: it sets every property via their private setters.
    private NotificationDispatch() : base(Guid.Empty) { }

    private NotificationDispatch(
        Guid id,
        NotificationKind kind,
        Guid? lessonId,
        DateOnly? localDate,
        long chatId,
        int? messageId,
        RenderedSnapshot snapshot,
        string contentHash,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset sentAtUtc)
        : base(id)
    {
        Kind = kind;
        LessonId = lessonId;
        LocalDate = localDate;
        ChatId = chatId;
        MessageId = messageId;
        Snapshot = snapshot;
        ContentHash = contentHash;
        State = DispatchState.Delivered;
        ExpiresAtUtc = expiresAtUtc;
        SentAtUtc = sentAtUtc;
        LastSyncedAtUtc = sentAtUtc;
    }

    /// <summary>
    /// Telegram id of the tutor this dispatch belongs to. Ownership / scope key: persistence stamps
    /// it from the scope's tenant on insert and filters every read by it, so nothing in the domain —
    /// or above it — has to carry the owner around.
    /// </summary>
    public long TutorTelegramId { get; private set; }

    public NotificationKind Kind { get; private set; }

    /// <summary>The lesson a reminder is about; null for a day digest.</summary>
    public Guid? LessonId { get; private set; }

    /// <summary>The tutor-local date a digest covers; null for a reminder.</summary>
    public DateOnly? LocalDate { get; private set; }

    /// <summary>The Telegram chat the message occupies — the tutor's own private chat with the bot.</summary>
    public long ChatId { get; private set; }

    /// <summary>
    /// The message that can be edited, or null when there is none to edit (a send that failed
    /// permanently, or a row seeded from the pre-dispatch era). Reconciliation still retires such a
    /// row; it just cannot rewrite it.
    /// </summary>
    public int? MessageId { get; private set; }

    /// <summary>What the message was last rendered from — see <see cref="RenderedSnapshot"/>.</summary>
    public RenderedSnapshot Snapshot { get; private set; } = RenderedSnapshot.Empty;

    /// <summary>
    /// Lower-case hex SHA-256 of the exact text plus every button label and payload that went out.
    /// An unchanged hash means the message on screen is already correct, so no edit is issued.
    /// </summary>
    public string ContentHash { get; private set; } = string.Empty;

    public DispatchState State { get; private set; }

    /// <summary>
    /// When this dispatch stops owing anything: a reminder expires at its lesson's end, a digest at
    /// the later of the local midnight following its date and the end of that day's last lesson.
    /// </summary>
    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset SentAtUtc { get; private set; }

    /// <summary>When reconciliation last re-rendered or retracted this row.</summary>
    public DateTimeOffset LastSyncedAtUtc { get; private set; }

    /// <summary>Still the live message for its target — the state a new dispatch is blocked by.</summary>
    public bool IsLive => State == DispatchState.Delivered;

    /// <summary>The reminder that just went out for <paramref name="lessonId"/>.</summary>
    public static NotificationDispatch ForReminder(
        Guid lessonId,
        long chatId,
        int? messageId,
        RenderedSnapshot snapshot,
        string contentHash,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset sentAtUtc) =>
        new(Guid.NewGuid(), NotificationKind.Reminder, lessonId, null, chatId, messageId,
            snapshot, contentHash, expiresAtUtc, sentAtUtc);

    /// <summary>
    /// The day digest that just went out for <paramref name="localDate"/>. A reminder is not a
    /// digest — it targets a lesson, not a date — so passing its kind here is a caller defect.
    /// </summary>
    public static NotificationDispatch ForDay(
        NotificationKind kind,
        DateOnly localDate,
        long chatId,
        int? messageId,
        RenderedSnapshot snapshot,
        string contentHash,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset sentAtUtc)
    {
        if (kind == NotificationKind.Reminder)
            throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "A reminder targets a lesson, not a local date.");

        return new NotificationDispatch(Guid.NewGuid(), kind, null, localDate, chatId, messageId,
            snapshot, contentHash, expiresAtUtc, sentAtUtc);
    }

    /// <summary>
    /// Records that the message was re-rendered: the snapshot and hash become what is on screen now.
    /// A retracted row has no message left to keep truthful, so re-syncing one is a caller defect.
    /// </summary>
    public void Resync(RenderedSnapshot snapshot, string contentHash, DateTimeOffset atUtc)
    {
        if (State == DispatchState.Retracted)
            throw new InvalidOperationException("A retracted dispatch can no longer be re-synced.");

        Snapshot = snapshot;
        ContentHash = contentHash;
        LastSyncedAtUtc = atUtc;
    }

    /// <summary>
    /// Retires the dispatch: it stops being the live message for its target, which frees a new one to
    /// take its place. Permanent and idempotent — retracting twice is the same statement.
    /// </summary>
    public void Retract(DateTimeOffset atUtc)
    {
        State = DispatchState.Retracted;
        LastSyncedAtUtc = atUtc;
    }
}
