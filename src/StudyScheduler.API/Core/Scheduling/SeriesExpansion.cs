using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Core.Scheduling;

/// <summary>One series and its expanded occurrences that survived materialization suppression.</summary>
public sealed record SeriesOccurrences(LessonSeries Series, List<LessonOccurrence> Occurrences);

/// <summary>
/// The shared read half of virtual recurrence: expands a tutor's active series into concrete
/// occurrences intersecting a UTC window, then suppresses slots that already have a physical
/// <see cref="Lesson"/> row (matched by <c>SeriesId</c> + <c>OccurrenceDate</c> — a physical
/// row governs its slot even when it was rescheduled outside the window). Consumed by
/// <see cref="LessonMaterializer"/> (schedule reads) and the overlap checker.
/// </summary>
public sealed class SeriesExpansion(
    ILessonRepository lessons,
    ILessonSeriesRepository seriesRepo)
{
    /// <summary>
    /// Per-series occurrences intersecting <c>(fromUtc, toUtc)</c> (strict — back-to-back slots
    /// do not intersect) that have no physical lesson row yet. Series without any free
    /// occurrence are omitted.
    /// </summary>
    public async Task<List<SeriesOccurrences>> GetFreeOccurrencesAsync(
        long tutorTelegramId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        Guid? studentId = null,
        CancellationToken ct = default)
    {
        // ±2 days of local-date slack around the UTC range covers any time-zone offset plus any
        // lesson duration (≤ 10 h); the exact UTC intersection filter below trims the excess.
        var fromLocal = DateOnly.FromDateTime(fromUtc.UtcDateTime).AddDays(-2);
        var toLocal = DateOnly.FromDateTime(toUtc.UtcDateTime).AddDays(2);

        // Expand occurrences in memory first, then resolve materialized dates for all relevant
        // series in one bulk query (instead of one query per series).
        var expanded = new List<SeriesOccurrences>();
        foreach (var series in await seriesRepo.GetActiveByTutorAsync(tutorTelegramId, ct))
        {
            if (studentId is { } sid && series.StudentId != sid)
                continue;

            var occurrences = series.GetOccurrences(fromLocal, toLocal)
                .Where(o => o.StartUtc < toUtc && o.EndUtc > fromUtc)
                .ToList();
            if (occurrences.Count > 0)
                expanded.Add(new SeriesOccurrences(series, occurrences));
        }

        if (expanded.Count == 0)
            return expanded;

        var materialized = GroupBySeries(await lessons.GetOccurrenceDatesForSeriesAsync(
            expanded.Select(e => e.Series.Id).ToList(), fromLocal, toLocal, ct));

        var free = new List<SeriesOccurrences>();
        foreach (var (series, occurrences) in expanded)
        {
            var taken = materialized.GetValueOrDefault(series.Id);
            var freeOccurrences = taken is null
                ? occurrences
                : occurrences.Where(o => !taken.Contains(o.OccurrenceDate)).ToList();
            if (freeOccurrences.Count > 0)
                free.Add(new SeriesOccurrences(series, freeOccurrences));
        }

        return free;
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
}
