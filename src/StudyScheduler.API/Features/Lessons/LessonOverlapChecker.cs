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
public sealed class LessonOverlapChecker(
    ILessonRepository lessons,
    ILessonSeriesRepository seriesRepo,
    ILogger<LessonOverlapChecker> logger)
{
    /// <summary>
    /// Series-vs-series conflicts are searched within this horizon from the start of the ranges'
    /// intersection; two open-ended series whose first collision is further out are not detected.
    /// </summary>
    private const int SeriesConflictHorizonDays = 728; // 104 weeks

    /// <summary>
    /// Conflicts for a single lesson slot (create or reschedule).
    /// <paramref name="excludeOccurrence"/> exempts one series slot from the virtual-occurrence
    /// check — used when that very slot is being materialized (its row is not saved yet, so it
    /// would otherwise conflict with itself).
    /// </summary>
    public async Task<List<LessonConflict>> CheckLessonAsync(
        long tutorTelegramId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid? excludeLessonId = null,
        (Guid SeriesId, DateOnly OccurrenceDate)? excludeOccurrence = null,
        CancellationToken ct = default)
    {
        var conflicts = new List<LessonConflict>();

        foreach (var lesson in await lessons.GetOverlappingAsync(tutorTelegramId, startUtc, endUtc, excludeLessonId, ct))
            conflicts.Add(FromLesson(lesson));

        // Unmaterialized occurrences of active series. ±2 days covers any duration (≤ 10 h) and
        // time-zone offset around the target slot.
        var fromLocal = DateOnly.FromDateTime(startUtc.UtcDateTime).AddDays(-2);
        var toLocal = DateOnly.FromDateTime(endUtc.UtcDateTime).AddDays(2);

        // Expand candidates in memory first, then fetch the materialized occurrence dates of all
        // relevant series in one bulk query (instead of one query per series).
        var candidates = new List<(LessonSeries Series, List<LessonOccurrence> Occurrences)>();
        foreach (var series in await seriesRepo.GetActiveByTutorAsync(tutorTelegramId, ct))
        {
            var occurrences = series.GetOccurrences(fromLocal, toLocal)
                .Where(o => o.StartUtc < endUtc && o.EndUtc > startUtc)
                .ToList();
            if (occurrences.Count > 0)
                candidates.Add((series, occurrences));
        }

        if (candidates.Count > 0)
        {
            // A materialized occurrence is governed by its concrete lesson (already checked above;
            // if it was cancelled or rescheduled away, the slot is free).
            var materialized = GroupBySeries(await lessons.GetOccurrenceDatesForSeriesAsync(
                candidates.Select(c => c.Series.Id).ToList(), fromLocal, toLocal, ct));

            foreach (var (series, occurrences) in candidates)
            {
                var taken = materialized.GetValueOrDefault(series.Id);
                conflicts.AddRange(occurrences
                    .Where(o => taken is null || !taken.Contains(o.OccurrenceDate))
                    .Where(o => excludeOccurrence is not { } excl
                        || excl.SeriesId != series.Id
                        || excl.OccurrenceDate != o.OccurrenceDate)
                    .Select(o => FromSeries(series, o)));
            }
        }

        if (conflicts.Count > 0)
            logger.LogInformation(
                "Detected {ConflictCount} scheduling conflicts for tutor {TutorTelegramId} in [{StartUtc}, {EndUtc})",
                conflicts.Count, tutorTelegramId, startUtc, endUtc);

        return conflicts;
    }

    /// <summary>Conflicts for a new series: against existing lessons and other active series.</summary>
    public async Task<List<LessonConflict>> CheckSeriesAsync(LessonSeries candidate, CancellationToken ct = default)
    {
        var conflicts = new List<LessonConflict>();

        // Existing lessons are finite, so this check has no horizon: compute the candidate's
        // occurrences across the span of the tutor's future lessons and compare in memory.
        var seriesStartUtc = new DateTimeOffset(
            candidate.StartDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).AddDays(-2);
        var futureLessons = await lessons.GetFromDateAsync(candidate.TutorTelegramId, seriesStartUtc, ct);
        if (futureLessons.Count > 0)
        {
            var minLocal = DateOnly.FromDateTime(futureLessons.Min(l => l.StartUtc).UtcDateTime).AddDays(-2);
            var maxLocal = DateOnly.FromDateTime(futureLessons.Max(l => l.EndUtc).UtcDateTime).AddDays(2);

            foreach (var occurrence in candidate.GetOccurrences(minLocal, maxLocal))
                conflicts.AddRange(futureLessons
                    .Where(l => l.StartUtc < occurrence.EndUtc && l.EndUtc > occurrence.StartUtc)
                    .Select(FromLesson));
        }

        foreach (var other in await seriesRepo.GetActiveByTutorAsync(candidate.TutorTelegramId, ct))
        {
            if (other.Id == candidate.Id)
                continue;

            if (FirstCollision(candidate, other) is { } collision)
                conflicts.Add(FromSeries(other, collision));
        }

        if (conflicts.Count > 0)
            logger.LogInformation(
                "Detected {ConflictCount} scheduling conflicts for tutor {TutorTelegramId} while creating a series starting {StartDate}",
                conflicts.Count, candidate.TutorTelegramId, candidate.StartDate);

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

    private static Dictionary<Guid, HashSet<DateOnly>> GroupBySeries(
        List<(Guid SeriesId, DateOnly OccurrenceDate)> rows)
    {
        var sets = new Dictionary<Guid, HashSet<DateOnly>>();
        foreach (var (seriesId, occurrenceDate) in rows)
        {
            if (!sets.TryGetValue(seriesId, out var set))
                sets[seriesId] = set = new HashSet<DateOnly>();
            set.Add(occurrenceDate);
        }

        return sets;
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
