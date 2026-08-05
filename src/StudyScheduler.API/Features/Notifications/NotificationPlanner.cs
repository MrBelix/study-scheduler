using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Notifications;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// One message that owes a FIRST send right now. A reminder addresses a lesson, a digest addresses a
/// tutor-local date — never both, exactly as <see cref="NotificationDispatch"/> stores it.
/// </summary>
public sealed record DueNotification(NotificationKind Kind, Guid? LessonId, DateOnly? LocalDate);

/// <summary>
/// Decides which messages owe a first send at a given instant. Pure and I/O-free — it reads the
/// tutor's opt-ins, the lessons the caller already fetched and the set of dispatches that are still
/// LIVE (the durable dedup, which lives on the dispatch rows rather than on the lesson) and returns
/// what is due now. A retracted dispatch is deliberately absent from <c>live</c>: that is what re-arms
/// a reminder after its lesson was moved.
/// Keeping the message truthful once it is on screen is reconciliation's job
/// (<see cref="NotificationReconciler"/>), not this one's — the planner only ever opens a new message.
/// </summary>
public sealed class NotificationPlanner
{
    /// <param name="localDayLessons">
    /// Every lesson whose LOCAL START DATE is the one day this call is about (a lesson belongs to the
    /// date its start falls on in the tutor's zone, so one running through midnight stays with the day
    /// it began). Empty means neither digest can be due.
    /// </param>
    /// <param name="reminderWindow">Lessons starting within <c>[now, now + RemindMinutes]</c>.</param>
    /// <param name="live">Every dispatch of this tutor that is still <c>Delivered</c>.</param>
    /// <param name="summaryGraceMinutes">
    /// Taken as a value rather than read from <c>IOptions</c> so this stays a pure function.
    /// </param>
    public IReadOnlyList<DueNotification> Plan(
        TutorProfile profile,
        IReadOnlyList<Lesson> localDayLessons,
        IReadOnlyList<Lesson> reminderWindow,
        IReadOnlyCollection<NotificationDispatch> live,
        DateTimeOffset nowUtc,
        int summaryGraceMinutes)
    {
        var due = new List<DueNotification>();
        AddDueReminders(profile, reminderWindow, live, nowUtc, due);

        // Both digests describe a day that has lessons in it; an empty day is never worth a message.
        if (localDayLessons.Count == 0)
            return due;

        var zone = profile.TimeZone;
        var localDate = LocalDateOf(localDayLessons[0].StartUtc, zone);
        var todayLocal = LocalDateOf(nowUtc, zone);

        if (IsAgendaDue(profile, localDate, todayLocal, live, nowUtc))
            due.Add(new DueNotification(NotificationKind.DayAgenda, null, localDate));

        if (IsSummaryDue(profile, localDayLessons, localDate, todayLocal, live, nowUtc, summaryGraceMinutes))
            due.Add(new DueNotification(NotificationKind.DaySummary, null, localDate));

        return due;
    }

    /// <summary>
    /// Every scheduled lesson whose lead time has opened and that has not started yet, minus the ones
    /// a live dispatch already covers. A cancelled or completed lesson is never reminded about.
    /// </summary>
    private static void AddDueReminders(
        TutorProfile profile,
        IReadOnlyList<Lesson> reminderWindow,
        IReadOnlyCollection<NotificationDispatch> live,
        DateTimeOffset nowUtc,
        List<DueNotification> due)
    {
        if (profile.RemindMinutes is not { } remind || reminderWindow.Count == 0)
            return;

        var alreadyLive = live
            .Where(d => d.Kind == NotificationKind.Reminder && d.LessonId is not null)
            .Select(d => d.LessonId!.Value)
            .ToHashSet();

        foreach (var lesson in reminderWindow)
        {
            if (lesson.Status != LessonStatus.Scheduled || alreadyLive.Contains(lesson.Id))
                continue;

            if (lesson.StartUtc.AddMinutes(-remind) <= nowUtc && nowUtc < lesson.StartUtc)
                due.Add(new DueNotification(NotificationKind.Reminder, lesson.Id, null));
        }
    }

    /// <summary>
    /// The morning agenda goes out once the tutor's own wall clock reaches their chosen hour, and only
    /// for the day they are actually living in — yesterday's agenda is yesterday's news, whatever the
    /// caller passes.
    /// </summary>
    private static bool IsAgendaDue(
        TutorProfile profile,
        DateOnly localDate,
        DateOnly todayLocal,
        IReadOnlyCollection<NotificationDispatch> live,
        DateTimeOffset nowUtc)
    {
        if (!profile.MorningAgenda || localDate != todayLocal)
            return false;

        var localNow = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, profile.TimeZone).DateTime);
        return localNow >= profile.MorningAgendaAtLocal
            && !HasLive(live, NotificationKind.DayAgenda, localDate);
    }

    /// <summary>
    /// The evening summary waits for the day's LAST lesson to end plus the grace period — so a
    /// 23:30–00:30 lesson holds its day open past local midnight — and only goes out when there is
    /// something left to mark. Nothing unmarked means nothing is sent: a fully closed day reaches its
    /// closed form by RE-rendering an existing summary, never by opening a new one.
    /// </summary>
    private static bool IsSummaryDue(
        TutorProfile profile,
        IReadOnlyList<Lesson> localDayLessons,
        DateOnly localDate,
        DateOnly todayLocal,
        IReadOnlyCollection<NotificationDispatch> live,
        DateTimeOffset nowUtc,
        int summaryGraceMinutes)
    {
        if (!profile.DaySummary)
            return false;

        var lastEndUtc = localDayLessons.Max(l => l.EndUtc);

        // A day the tutor has already left behind is only still this tick's business while it spills
        // past local midnight; otherwise a summary retired at midnight would be re-opened the next day.
        if (localDate != todayLocal
            && lastEndUtc < WallClock.ToUtc(todayLocal, TimeOnly.MinValue, profile.TimeZone))
            return false;

        return lastEndUtc.AddMinutes(summaryGraceMinutes) <= nowUtc
            && localDayLessons.Any(l => l.Status == LessonStatus.Scheduled)
            && !HasLive(live, NotificationKind.DaySummary, localDate);
    }

    private static bool HasLive(
        IReadOnlyCollection<NotificationDispatch> live, NotificationKind kind, DateOnly localDate) =>
        live.Any(d => d.Kind == kind && d.LocalDate == localDate);

    private static DateOnly LocalDateOf(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}
