using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Core.Scheduling;

/// <summary>One series and its expanded occurrences that have no physical lesson row yet.</summary>
public sealed record SeriesOccurrences(LessonSeries Series, IReadOnlyList<LessonOccurrence> Occurrences);

/// <summary>
/// The shared read half of virtual recurrence: expands a tutor's active series into concrete
/// occurrences intersecting a UTC window, then suppresses slots that already have a physical
/// <see cref="Lesson"/> row (matched by <see cref="SeriesSlot"/>). Never writes. Consumed by the
/// schedule reader and the overlap checker.
/// </summary>
public sealed class SeriesExpansion(
    ILessonRepository lessons,
    ILessonSeriesRepository seriesRepo)
{
    /// <summary>
    /// Per-series occurrences intersecting <c>(fromUtc, toUtc)</c> (strict — back-to-back slots do
    /// not intersect) that have no physical lesson row yet. Series without any free occurrence are
    /// omitted.
    /// </summary>
    public async Task<IReadOnlyList<SeriesOccurrences>> GetFreeOccurrencesAsync(
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

        var expanded = new List<SeriesOccurrences>();
        foreach (var series in await seriesRepo.GetActiveByTutorAsync(tutorTelegramId, fromLocal, ct))
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

        // Resolve materialized slots for all relevant series in one bulk query.
        var taken = (await lessons.GetMaterializedSlotsAsync(
                expanded.Select(e => e.Series.Id).ToList(), fromLocal, toLocal, ct))
            .GroupBy(s => s.SeriesId)
            .ToDictionary(g => g.Key, g => g.Select(s => s.OccurrenceDate).ToHashSet());

        var free = new List<SeriesOccurrences>();
        foreach (var (series, occurrences) in expanded)
        {
            var takenDates = taken.GetValueOrDefault(series.Id);
            var freeOccurrences = takenDates is null
                ? occurrences
                : occurrences.Where(o => !takenDates.Contains(o.OccurrenceDate)).ToList();
            if (freeOccurrences.Count > 0)
                free.Add(new SeriesOccurrences(series, freeOccurrences));
        }

        return free;
    }
}
