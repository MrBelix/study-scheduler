using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// The lifecycle phase a reminder message renders for. Derived by <see cref="NotificationViewBuilder"/>
/// from the lesson's current status, the clock, and the difference against the previous snapshot — not
/// stored anywhere of its own. <see cref="Removed"/> is the one phase that can be reached with no
/// current lesson at all.
/// </summary>
public enum ReminderPhase { BeforeStart, Started, Moved, Cancelled, Completed, Removed }

/// <summary>
/// Everything <see cref="NotificationRenderer.Reminder"/> needs to produce a reminder message — pure
/// data, no I/O. <paramref name="OriginalStartLocal"/> and <paramref name="WasPaidBeforeChange"/> come
/// from the PREVIOUS snapshot (frozen at first send, copied forward by the builder): they are what let
/// N2 say "було {oldStart}" and N3 say "оплата лишається" after the domain clears <c>IsPaid</c> on
/// cancel. <paramref name="NowLocal"/> is the tutor-local instant the message is being rendered for —
/// it is what N2's "було {oldDayWord} {oldStart}" is worded relative to (today/tomorrow/an absolute
/// date), never the NEW start's own date, or a same-day-to-tomorrow move would misreport "today" as an
/// absolute weekday.
/// </summary>
public sealed record ReminderView(
    AppLanguage Lang, Guid LessonId, ReminderPhase Phase, int RemindMinutes,
    string StudentName, string? Topic,
    DateTimeOffset StartLocal, DateTimeOffset EndLocal, int DurationMinutes,
    decimal Price, bool IsPaid,
    DateTimeOffset? OriginalStartLocal, bool WasPaidBeforeChange, DateTimeOffset NowLocal);

/// <summary>One lesson line of a day digest (agenda or summary) — pure data.</summary>
public sealed record DayLineView(
    Guid LessonId, DateTimeOffset StartLocal, DateTimeOffset EndLocal, string StudentName,
    string? Topic, LessonStatus Status, bool IsPaid, decimal Price,
    decimal StudentDebt, string? SeriesRule);

/// <summary>
/// Everything <see cref="NotificationRenderer.Agenda"/> needs. <paramref name="BaselineLessonIds"/> and
/// <paramref name="BaselineStarts"/> are the morning's original composition, copied forward from the
/// previous snapshot, so a re-render can say "+1 урок, 1 скасовано, 1 перенесено".
/// </summary>
public sealed record AgendaView(
    AppLanguage Lang, DateOnly LocalDate, IReadOnlyList<DayLineView> Lines,
    IReadOnlyList<Guid> BaselineLessonIds,
    IReadOnlyDictionary<Guid, DateTimeOffset> BaselineStarts,
    DateTimeOffset? UpdatedAtLocal);

/// <summary>Everything <see cref="NotificationRenderer.Summary"/> needs for either its list or its focus step.</summary>
public sealed record SummaryView(
    AppLanguage Lang, DateOnly LocalDate, IReadOnlyList<DayLineView> Lines,
    Guid? FocusedLessonId, bool Expanded, int TomorrowLessonsCount);
