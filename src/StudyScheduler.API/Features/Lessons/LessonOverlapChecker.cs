using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>
/// The tail of an existing series a candidate takes over: everything <paramref name="SeriesId"/>
/// scheduled from <paramref name="FromLocal"/> on is about to cease to exist, so it must not be
/// reported as a conflict against its own replacement.
/// </summary>
/// <param name="SeriesId">The series being replaced.</param>
/// <param name="FromLocal">First local date the replacement takes over.</param>
public sealed record ReplacedSeries(Guid SeriesId, DateOnly FromLocal);

/// <summary>
/// Detects time conflicts before a lesson or series is written. A single slot is checked
/// against the rows themselves — a series has already generated its lessons across the planning
/// horizon, and a lesson may not be placed beyond it, so every occupied moment is a row. A new
/// SERIES is checked analytically as well (computing occurrences), because a rule runs past the
/// horizon its rows stop at.
///
/// Everything it reads belongs to the current tenant by construction, so only one tutor's calendar is
/// ever compared — the id is read here for the log line and nothing else.
///
/// Student status is none of its business: archiving a student ends their series and deletes what
/// they had ahead (see <see cref="LessonService.StopTeachingAsync"/>), and nothing books, re-opens
/// or un-settles a lesson back onto their schedule while they stay archived. A completed row the
/// cascade kept as history is reported like any other: that time genuinely went to a lesson that
/// happened.
///
/// The check-then-insert flow can race (no range-exclusion constraint backs it), but the tenant is
/// a single human tutor — the realistic race is a double-click — so this is accepted.
/// </summary>
public sealed class LessonOverlapChecker(
    ILessonRepository lessons,
    ILessonSeriesRepository seriesRepo,
    ITutorContext tutor,
    ILogger<LessonOverlapChecker> logger)
{
    /// <summary>
    /// Series-vs-series conflicts are searched within this horizon from the start of the ranges'
    /// intersection; two open-ended series whose first collision is further out are not detected.
    /// </summary>
    private const int SeriesConflictHorizonDays = 728; // 104 weeks

    /// <summary>Conflicts for a single lesson slot (create or reschedule).</summary>
    public async Task<IReadOnlyList<LessonConflict>> CheckLessonAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid? excludeLessonId = null,
        CancellationToken ct = default)
    {
        var conflicts = (await lessons.GetOverlappingAsync(startUtc, endUtc, excludeLessonId, ct))
            .Select(FromLesson)
            .ToList();

        if (conflicts.Count > 0)
            logger.LogInformation(
                "Detected {ConflictCount} scheduling conflicts for tutor {TutorTelegramId} in [{StartUtc}, {EndUtc})",
                conflicts.Count, tutor.CurrentTutorTelegramId, startUtc, endUtc);

        return conflicts;
    }

    /// <summary>
    /// Conflicts for a new series: against existing lessons and other active series.
    /// <paramref name="replaced"/> names the tail of a series this candidate supersedes (the newly
    /// exposed window of an extension), so nothing that tail still owns — neither its remaining
    /// occurrences nor the rows generated from them — is reported against it.
    /// </summary>
    public async Task<IReadOnlyList<LessonConflict>> CheckSeriesAsync(
        LessonSeries candidate,
        ReplacedSeries? replaced = null,
        CancellationToken ct = default)
    {
        var conflicts = new List<LessonConflict>();

        // Existing lessons are finite, so this check has no horizon: compute the candidate's
        // occurrences across the span of the tutor's future lessons and compare in memory.
        var seriesStartUtc = new DateTimeOffset(
            candidate.StartDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).AddDays(-2);
        var futureLessons = (await lessons.GetFromDateAsync(seriesStartUtc, ct))
            .Where(l => !IsReplaced(l, replaced))
            .ToList();
        if (futureLessons.Count > 0)
        {
            var minLocal = DateOnly.FromDateTime(futureLessons.Min(l => l.StartUtc).UtcDateTime).AddDays(-2);
            var maxLocal = DateOnly.FromDateTime(futureLessons.Max(l => l.EndUtc).UtcDateTime).AddDays(2);

            foreach (var occurrence in candidate.GetOccurrences(minLocal, maxLocal))
                conflicts.AddRange(futureLessons
                    .Where(l => l.StartUtc < occurrence.EndUtc && l.EndUtc > occurrence.StartUtc)
                    .Select(FromLesson));
        }

        // A series ended before the candidate starts can't collide — skip it in the query. Rules are
        // compared rule-to-rule: they outlive the rows they have generated so far.
        foreach (var other in await seriesRepo.GetActiveAsync(candidate.StartDate, ct))
        {
            if (other.Id == candidate.Id)
                continue;

            // The replaced series is still stored with its old window (nothing is saved before this
            // check runs), so its superseded occurrences are cut off here instead.
            var cutoff = replaced is { } r && r.SeriesId == other.Id ? r.FromLocal : (DateOnly?)null;
            if (FirstCollision(candidate, other, cutoff) is { } collision)
                conflicts.Add(FromSeries(other, collision));
        }

        if (conflicts.Count > 0)
            logger.LogInformation(
                "Detected {ConflictCount} scheduling conflicts for tutor {TutorTelegramId} while creating a series starting {StartDate}",
                conflicts.Count, tutor.CurrentTutorTelegramId, candidate.StartDate);

        return conflicts;
    }

    /// <summary>
    /// Whether the lesson was generated by the part of a series the candidate replaces. Such a row is
    /// either deleted with that part or kept as history — never a reason to refuse the replacement it
    /// belongs to.
    /// </summary>
    private static bool IsReplaced(Lesson lesson, ReplacedSeries? replaced) =>
        replaced is { } r && lesson.SeriesId == r.SeriesId && lesson.OccurrenceDate >= r.FromLocal;

    /// <summary>
    /// First occurrence of <paramref name="other"/> that collides with <paramref name="candidate"/>,
    /// comparing concrete UTC occurrences over the intersection of their date ranges (capped by the
    /// horizon) — exact across DST and differing time zones. Occurrences of <paramref name="other"/>
    /// on or after <paramref name="otherCutoff"/> are ignored: the candidate is taking them over.
    /// </summary>
    private static LessonOccurrence? FirstCollision(LessonSeries candidate, LessonSeries other, DateOnly? otherCutoff)
    {
        var fromLocal = Max(candidate.StartDate, other.StartDate).AddDays(-1);
        var horizon = fromLocal.AddDays(SeriesConflictHorizonDays);
        var toLocal = Min(Min(candidate.EndDate, other.EndDate) ?? horizon, horizon).AddDays(1);
        if (toLocal < fromLocal)
            return null;

        var candidateOccurrences = candidate.GetOccurrences(fromLocal, toLocal);
        foreach (var occurrence in other.GetOccurrences(fromLocal, toLocal))
        {
            if (otherCutoff is { } cutoff && occurrence.OccurrenceDate >= cutoff)
                continue;

            if (candidateOccurrences.Any(c => c.StartUtc < occurrence.EndUtc && c.EndUtc > occurrence.StartUtc))
                return occurrence;
        }

        return null;
    }

    // The lesson is reported under its own id — the identifier GET/PATCH /lessons/{id} takes,
    // whether the row came from a series or was placed by hand.
    private static LessonConflict FromLesson(Lesson lesson) =>
        new(lesson.Id, lesson.SeriesId, null, lesson.StartUtc, lesson.EndUtc);

    private static LessonConflict FromSeries(LessonSeries series, LessonOccurrence occurrence) =>
        new(null, series.Id, series.Title, occurrence.StartUtc, occurrence.EndUtc);

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static DateOnly? Min(DateOnly? a, DateOnly? b) =>
        (a, b) switch
        {
            (null, _) => b,
            (_, null) => a,
            _ => a < b ? a : b,
        };

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
