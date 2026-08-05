using StudyScheduler.API.Features.Reports;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Notifications;
using StudyScheduler.Domain.Students;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Notifications;

/// <summary>
/// Reads the data one render needs and turns it into a view (<see cref="ReminderView"/>,
/// <see cref="AgendaView"/> or <see cref="SummaryView"/>) for <see cref="NotificationRenderer"/>. Every
/// read below goes through the current tenant's repositories, so the builder never takes — or needs — a
/// tutor id of its own. A lesson belongs to the local date of its START in the tutor's zone, so the day
/// queries filter the range result down to that.
/// </summary>
public sealed class NotificationViewBuilder(
    ILessonRepository lessons,
    IStudentRepository students,
    ILessonSeriesRepository series,
    IStudentDebtReader debts,
    TimeProvider clock)
{
    /// <summary>
    /// The current reminder for <paramref name="lessonId"/>, or null when there is nothing to render
    /// (the lesson is gone AND no <paramref name="previous"/> snapshot exists to render its removal
    /// from either).
    /// </summary>
    public async Task<ReminderView?> BuildReminderAsync(
        Guid lessonId, TutorProfile profile, RenderedSnapshot? previous, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var lang = profile.LanguageCode ?? AppLanguage.Uk;
        var zone = profile.TimeZone;
        var lesson = await lessons.GetByIdAsync(lessonId, ct: ct);
        var prevReminder = previous?.Reminder;

        if (lesson is null)
        {
            if (prevReminder is null)
                return null;

            var missingEnd = prevReminder.StartUtc.AddMinutes(prevReminder.DurationMinutes);
            return new ReminderView(
                lang, lessonId, ReminderPhase.Removed, prevReminder.RemindMinutes,
                prevReminder.StudentName, prevReminder.Topic,
                Local(prevReminder.StartUtc, zone), Local(missingEnd, zone), prevReminder.DurationMinutes,
                prevReminder.Price, prevReminder.IsPaid,
                Local(prevReminder.OriginalStartUtc, zone), prevReminder.IsPaid, Local(nowUtc, zone));
        }

        var student = await students.GetByIdAsync(lesson.StudentId, ct: ct);
        var studentName = student?.Name ?? string.Empty;

        var remindMinutes = prevReminder?.RemindMinutes ?? profile.RemindMinutes ?? TutorProfile.DefaultRemindMinutes;
        var originalStartUtc = prevReminder?.OriginalStartUtc ?? lesson.StartUtc;
        var wasPaidBeforeChange = prevReminder?.IsPaid ?? lesson.IsPaid;

        var phase = lesson.Status switch
        {
            LessonStatus.Cancelled => ReminderPhase.Cancelled,
            LessonStatus.Completed => ReminderPhase.Completed,
            _ when prevReminder is not null && lesson.StartUtc != originalStartUtc => ReminderPhase.Moved,
            _ when nowUtc >= lesson.StartUtc => ReminderPhase.Started,
            _ => ReminderPhase.BeforeStart,
        };

        return new ReminderView(
            lang, lesson.Id, phase, remindMinutes, studentName, lesson.Topic,
            Local(lesson.StartUtc, zone), Local(lesson.EndUtc, zone), lesson.DurationMinutes,
            lesson.Price, lesson.IsPaid, Local(originalStartUtc, zone), wasPaidBeforeChange, Local(nowUtc, zone));
    }

    public async Task<AgendaView> BuildAgendaAsync(
        DateOnly localDate, TutorProfile profile, RenderedSnapshot? previous, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var lang = profile.LanguageCode ?? AppLanguage.Uk;
        var lines = await BuildDayLinesAsync(localDate, profile, ct);

        var baselineLessonIds = previous?.Day?.BaselineLessonIds ?? [];
        var baselineStarts = previous?.Day?.BaselineStarts ?? new Dictionary<Guid, DateTimeOffset>();

        // "оновлено {hh:mm}" must be the instant the delta against the baseline last actually CHANGED,
        // not a live read of the clock — a live read would repaint the message (and cost a Telegram
        // edit) every single poll tick for the rest of the day, even with nothing new to report. So the
        // delta against the baseline is recomputed both for the CURRENT lines and for the lines the
        // previous snapshot was rendered from; only when they differ does the timestamp advance to now,
        // otherwise the previously-frozen instant is copied forward.
        DateTimeOffset? updatedAtLocal = null;
        if (previous?.Day is { } previousDay)
        {
            var currentDelta = ComputeAgendaDelta(lines, baselineLessonIds, baselineStarts);
            var previousDelta = ComputeAgendaDelta(previousDay.Lines, baselineLessonIds, baselineStarts);
            var frozenAtUtc = currentDelta == previousDelta ? previousDay.UpdatedAtUtc : null;
            updatedAtLocal = Local(frozenAtUtc ?? clock.GetUtcNow(), profile.TimeZone);
        }

        return new AgendaView(lang, localDate, lines, baselineLessonIds, baselineStarts, updatedAtLocal);
    }

    /// <summary>
    /// The (added, cancelled, moved) counts of <paramref name="currentLines"/> against the frozen
    /// baseline — the same shape <see cref="NotificationRenderer"/> renders into "+1 урок, 1 скасовано,
    /// 1 перенесено". Works over either the live <see cref="DayLineView"/> lines or a previous render's
    /// <see cref="DayLineSnapshot"/> lines, so it can compare "then" against "now".
    /// </summary>
    private static (int Added, int Cancelled, int Moved) ComputeAgendaDelta(
        IEnumerable<(Guid LessonId, DateTimeOffset StartUtc, LessonStatus Status)> currentLines,
        IReadOnlyCollection<Guid> baselineLessonIds,
        IReadOnlyDictionary<Guid, DateTimeOffset> baselineStarts)
    {
        var baseline = baselineLessonIds.ToHashSet();
        var list = currentLines.ToList();
        var added = list.Count(l => !baseline.Contains(l.LessonId));
        var cancelled = list.Count(l => l.Status == LessonStatus.Cancelled && baseline.Contains(l.LessonId));
        var moved = list.Count(l =>
            baseline.Contains(l.LessonId)
            && baselineStarts.TryGetValue(l.LessonId, out var baselineStart)
            && baselineStart != l.StartUtc);
        return (added, cancelled, moved);
    }

    private static (int Added, int Cancelled, int Moved) ComputeAgendaDelta(
        IReadOnlyList<DayLineView> currentLines,
        IReadOnlyCollection<Guid> baselineLessonIds,
        IReadOnlyDictionary<Guid, DateTimeOffset> baselineStarts) =>
        ComputeAgendaDelta(
            currentLines.Select(l => (l.LessonId, l.StartLocal.ToUniversalTime(), l.Status)),
            baselineLessonIds, baselineStarts);

    private static (int Added, int Cancelled, int Moved) ComputeAgendaDelta(
        IReadOnlyList<DayLineSnapshot> currentLines,
        IReadOnlyCollection<Guid> baselineLessonIds,
        IReadOnlyDictionary<Guid, DateTimeOffset> baselineStarts) =>
        ComputeAgendaDelta(
            currentLines.Select(l => (l.LessonId, l.StartUtc, l.Status)),
            baselineLessonIds, baselineStarts);

    public async Task<SummaryView> BuildSummaryAsync(
        DateOnly localDate, TutorProfile profile, Guid? focusedLessonId, bool expanded, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var lang = profile.LanguageCode ?? AppLanguage.Uk;
        var zone = profile.TimeZone;
        var lines = await BuildDayLinesAsync(localDate, profile, ct);

        var tomorrow = localDate.AddDays(1);
        var tomorrowRaw = await lessons.GetInRangeAsync(
            WallClock.ToUtc(tomorrow, TimeOnly.MinValue, zone),
            WallClock.ToUtc(tomorrow.AddDays(1), TimeOnly.MinValue, zone),
            ct: ct);
        var tomorrowCount = tomorrowRaw.Count(l =>
            l.Status != LessonStatus.Cancelled && LocalDateOf(l.StartUtc, zone) == tomorrow);

        return new SummaryView(lang, localDate, lines, focusedLessonId, expanded, tomorrowCount);
    }

    /// <summary>
    /// The lines of one local day, shared by the agenda and the summary: every lesson whose START falls
    /// on <paramref name="localDate"/> in the tutor's zone (a lesson spilling in from the previous day
    /// is dropped, one that spills into the next day stays), with its student's aggregate debt and — for
    /// a small enough day — its series' weekly description attached.
    /// </summary>
    private async Task<IReadOnlyList<DayLineView>> BuildDayLinesAsync(
        DateOnly localDate, TutorProfile profile, CancellationToken ct)
    {
        var zone = profile.TimeZone;
        var lang = profile.LanguageCode ?? AppLanguage.Uk;

        var raw = await lessons.GetInRangeAsync(
            WallClock.ToUtc(localDate, TimeOnly.MinValue, zone),
            WallClock.ToUtc(localDate.AddDays(1), TimeOnly.MinValue, zone),
            ct: ct);
        var dayLessons = raw
            .Where(l => LocalDateOf(l.StartUtc, zone) == localDate)
            .OrderBy(l => l.StartUtc)
            .ToList();

        if (dayLessons.Count == 0)
            return [];

        var studentIds = dayLessons.Select(l => l.StudentId).Distinct().ToList();
        var studentNames = (await students.GetByIdsAsync(studentIds, ct)).ToDictionary(s => s.Id, s => s.Name);
        var debtByStudent = (await debts.GetAllTimeAsync(ct)).ToDictionary(d => d.StudentId, d => d.Amount);

        var seriesRules = new Dictionary<Guid, string>();
        if (dayLessons.Count <= 6 && dayLessons.Any(l => l.SeriesId is not null))
        {
            var seriesIds = dayLessons.Where(l => l.SeriesId is not null).Select(l => l.SeriesId!.Value).ToHashSet();
            var allSeries = await series.GetAllAsync(ct);
            seriesRules = allSeries
                .Where(s => seriesIds.Contains(s.Id))
                .ToDictionary(s => s.Id, s => NotificationCopy.WeeklyRule(lang, s.Pattern.Days));
        }

        return [.. dayLessons.Select(l => new DayLineView(
            l.Id,
            Local(l.StartUtc, zone),
            Local(l.EndUtc, zone),
            studentNames.GetValueOrDefault(l.StudentId, string.Empty),
            l.Topic,
            l.Status,
            l.IsPaid,
            l.Price,
            debtByStudent.GetValueOrDefault(l.StudentId, 0m),
            l.SeriesId is { } seriesId ? seriesRules.GetValueOrDefault(seriesId) : null))];
    }

    private static DateTimeOffset Local(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone);

    private static DateOnly LocalDateOf(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}
