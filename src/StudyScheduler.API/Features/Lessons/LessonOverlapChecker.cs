using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>
/// Detects time conflicts for a tutor before a lesson or series is written. Checks concrete
/// lessons via SQL and active series analytically (computing their occurrences), so slots of
/// open-ended series are protected even before they are materialized.
///
/// The check-then-insert flow can race (SQL Server has no range-exclusion constraint), but the
/// tenant is a single human tutor — the realistic race is a double-click — so this is accepted.
/// </summary>
public sealed class LessonOverlapChecker(ILessonRepository lessons, ILessonSeriesRepository seriesRepo)
{
    /// <summary>
    /// Series-vs-series conflicts are searched within this horizon from the start of the ranges'
    /// intersection; two open-ended series whose first collision is further out are not detected.
    /// </summary>
    private const int SeriesConflictHorizonDays = 728; // 104 weeks

    /// <summary>Conflicts for a single lesson slot (create or reschedule).</summary>
    public async Task<List<LessonConflict>> CheckLessonAsync(
        long tutorTelegramId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid? excludeLessonId = null)
    {
        var conflicts = new List<LessonConflict>();

        foreach (var lesson in await lessons.GetOverlappingAsync(tutorTelegramId, startUtc, endUtc, excludeLessonId))
            conflicts.Add(FromLesson(lesson));

        // Unmaterialized occurrences of active series. ±2 days covers any duration (≤ 10 h) and
        // time-zone offset around the target slot.
        var fromLocal = DateOnly.FromDateTime(startUtc.UtcDateTime).AddDays(-2);
        var toLocal = DateOnly.FromDateTime(endUtc.UtcDateTime).AddDays(2);

        foreach (var series in await seriesRepo.GetActiveByTutorAsync(tutorTelegramId))
        {
            var candidates = series.GetOccurrences(fromLocal, toLocal)
                .Where(o => o.StartUtc < endUtc && o.EndUtc > startUtc)
                .ToList();
            if (candidates.Count == 0)
                continue;

            // A materialized occurrence is governed by its concrete lesson (already checked above;
            // if it was cancelled or rescheduled away, the slot is free).
            var materialized = (await lessons.GetOccurrenceDatesAsync(series.Id, fromLocal, toLocal)).ToHashSet();
            conflicts.AddRange(candidates
                .Where(o => !materialized.Contains(o.OccurrenceDate))
                .Select(o => FromSeries(series, o)));
        }

        return conflicts;
    }

    /// <summary>Conflicts for a new series: against existing lessons and other active series.</summary>
    public async Task<List<LessonConflict>> CheckSeriesAsync(LessonSeries candidate)
    {
        var conflicts = new List<LessonConflict>();

        // Existing lessons are finite, so this check has no horizon: compute the candidate's
        // occurrences across the span of the tutor's future lessons and compare in memory.
        var seriesStartUtc = new DateTimeOffset(
            candidate.StartDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).AddDays(-2);
        var futureLessons = await lessons.GetFromDateAsync(candidate.TutorTelegramId, seriesStartUtc);
        if (futureLessons.Count > 0)
        {
            var minLocal = DateOnly.FromDateTime(futureLessons.Min(l => l.StartUtc).UtcDateTime).AddDays(-2);
            var maxLocal = DateOnly.FromDateTime(futureLessons.Max(l => l.EndUtc).UtcDateTime).AddDays(2);

            foreach (var occurrence in candidate.GetOccurrences(minLocal, maxLocal))
                conflicts.AddRange(futureLessons
                    .Where(l => l.StartUtc < occurrence.EndUtc && l.EndUtc > occurrence.StartUtc)
                    .Select(FromLesson));
        }

        foreach (var other in await seriesRepo.GetActiveByTutorAsync(candidate.TutorTelegramId))
        {
            if (other.Id == candidate.Id)
                continue;

            if (FirstCollision(candidate, other) is { } collision)
                conflicts.Add(FromSeries(other, collision));
        }

        return conflicts;
    }

    /// <summary>
    /// First occurrence of <paramref name="other"/> that collides with <paramref name="candidate"/>,
    /// comparing concrete UTC occurrences over the intersection of their date ranges (capped by the
    /// horizon) — exact across DST and differing time zones.
    /// </summary>
    private static LessonOccurrence? FirstCollision(LessonSeries candidate, LessonSeries other)
    {
        var fromLocal = Max(candidate.StartDate, other.StartDate).AddDays(-1);
        var horizon = fromLocal.AddDays(SeriesConflictHorizonDays);
        var toLocal = Min(Min(candidate.EndDate, other.EndDate) ?? horizon, horizon).AddDays(1);
        if (toLocal < fromLocal)
            return null;

        var candidateOccurrences = candidate.GetOccurrences(fromLocal, toLocal);
        foreach (var occurrence in other.GetOccurrences(fromLocal, toLocal))
        {
            if (candidateOccurrences.Any(c => c.StartUtc < occurrence.EndUtc && c.EndUtc > occurrence.StartUtc))
                return occurrence;
        }

        return null;
    }

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
