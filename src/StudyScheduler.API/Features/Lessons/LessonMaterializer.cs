using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>
/// The two halves of virtual recurrence:
/// <para>
/// <b>Read</b> — <see cref="GetScheduleAsync"/> expands active series into virtual slots in
/// memory for a requested UTC range and merges them with the physical <see cref="Lesson"/> rows;
/// a physical row always wins its slot (matched by <c>SeriesId</c> + <c>OccurrenceDate</c>).
/// Nothing is written on reads.
/// </para>
/// <para>
/// <b>Write</b> — <see cref="MaterializeSlotAsync"/> turns one virtual slot into a physical
/// <see cref="Lesson"/> the moment it is first modified (topic/description, cancel, reschedule).
/// The unique <c>(SeriesId, OccurrenceDate)</c> index keeps this idempotent under races.
/// </para>
/// </summary>
public sealed class LessonMaterializer(
    ILessonRepository lessons,
    ILessonSeriesRepository seriesRepo,
    IStudentRepository students,
    TimeProvider clock,
    ILogger<LessonMaterializer> logger)
{
    /// <summary>
    /// The tutor's merged schedule intersecting <c>[fromUtc, toUtc)</c>: physical lessons plus
    /// virtual slots of active series that have no physical counterpart, ordered by start.
    /// </summary>
    public async Task<List<LessonResponse>> GetScheduleAsync(
        long tutorTelegramId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        Guid? studentId = null,
        CancellationToken ct = default)
    {
        var physical = await lessons.GetByTutorInRangeAsync(tutorTelegramId, fromUtc, toUtc, studentId, ct);
        var slots = physical.Select(LessonResponse.From).ToList();

        // ±1 day of local-date slack around the UTC range covers any time-zone offset.
        var fromLocal = DateOnly.FromDateTime(fromUtc.UtcDateTime).AddDays(-1);
        var toLocal = DateOnly.FromDateTime(toUtc.UtcDateTime).AddDays(1);

        // Expand occurrences in memory first, then resolve materialized dates and student rates
        // for all relevant series in two bulk queries (instead of a pair of queries per series).
        var expanded = new List<(LessonSeries Series, List<LessonOccurrence> Occurrences)>();
        foreach (var series in await seriesRepo.GetActiveByTutorAsync(tutorTelegramId, ct))
        {
            if (studentId is { } sid && series.StudentId != sid)
                continue;

            var occurrences = series.GetOccurrences(fromLocal, toLocal)
                .Where(o => o.StartUtc < toUtc && o.EndUtc > fromUtc)
                .ToList();
            if (occurrences.Count > 0)
                expanded.Add((series, occurrences));
        }

        if (expanded.Count == 0)
            return slots.OrderBy(s => s.StartUtc).ToList();

        // A physical row governs its slot even when it was rescheduled outside the requested
        // range, so suppression is checked by occurrence date, not against the rows above.
        var materialized = GroupBySeries(await lessons.GetOccurrenceDatesForSeriesAsync(
            expanded.Select(e => e.Series.Id).ToList(), fromLocal, toLocal, ct));

        var rates = await ResolveRatesAsync(tutorTelegramId, expanded.Select(e => e.Series), ct);

        foreach (var (series, occurrences) in expanded)
        {
            var taken = materialized.GetValueOrDefault(series.Id);
            var virtualSlots = taken is null
                ? occurrences
                : occurrences.Where(o => !taken.Contains(o.OccurrenceDate)).ToList();
            if (virtualSlots.Count == 0)
                continue;

            // Data anomaly guard: a series whose student is missing from the bulk lookup
            // must not take the whole schedule down with a 500.
            var price = series.Price ?? rates.GetValueOrDefault(series.StudentId);
            slots.AddRange(virtualSlots.Select(o => LessonResponse.Virtual(series, o, price)));
        }

        return slots.OrderBy(s => s.StartUtc).ToList();
    }

    /// <summary>
    /// Instantiates (without saving) the physical <see cref="Lesson"/> for one virtual slot,
    /// carrying the series link and the price snapshot. The caller applies the mutation that
    /// triggered materialization and persists.
    /// </summary>
    public async Task<Lesson> MaterializeSlotAsync(
        LessonSeries series,
        LessonOccurrence occurrence,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "Materializing slot {OccurrenceDate} of series {SeriesId} for tutor {TutorTelegramId}",
            occurrence.OccurrenceDate, series.Id, series.TutorTelegramId);

        return Lesson.Create(
            series.TutorTelegramId,
            series.StudentId,
            occurrence.StartUtc,
            series.DurationMinutes,
            await ResolvePriceAsync(series, ct),
            clock.GetUtcNow(),
            seriesId: series.Id,
            occurrenceDate: occurrence.OccurrenceDate);
    }

    /// <summary>Price snapshot: the series' own price, or the student's current rate.</summary>
    private async Task<decimal> ResolvePriceAsync(LessonSeries series, CancellationToken ct) =>
        series.Price ?? (await students.GetByIdAsync(series.StudentId, ct))!.Rate;

    /// <summary>Current rates of the students behind series without their own price, in one query.</summary>
    private async Task<Dictionary<Guid, decimal>> ResolveRatesAsync(
        long tutorTelegramId,
        IEnumerable<LessonSeries> series,
        CancellationToken ct)
    {
        var studentIds = series
            .Where(s => s.Price is null)
            .Select(s => s.StudentId)
            .Distinct()
            .ToList();
        if (studentIds.Count == 0)
            return new Dictionary<Guid, decimal>();

        return (await students.GetByIdsAsync(tutorTelegramId, studentIds, ct))
            .ToDictionary(s => s.Id, s => s.Rate);
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
