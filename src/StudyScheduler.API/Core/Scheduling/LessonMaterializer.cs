using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Core.Scheduling;

/// <summary>
/// The two halves of virtual recurrence:
/// <para>
/// <b>Read</b> — <see cref="GetScheduleAsync"/> expands active series into virtual slots in
/// memory for a requested UTC range (via <see cref="SeriesExpansion"/>) and merges them with the
/// physical <see cref="Lesson"/> rows; a physical row always wins its slot (matched by
/// <c>SeriesId</c> + <c>OccurrenceDate</c>). Nothing is written on reads.
/// </para>
/// <para>
/// <b>Write</b> — <see cref="MaterializeSlotAsync"/> turns one virtual slot into a physical
/// <see cref="Lesson"/> the moment it is first modified (topic/description, cancel, reschedule).
/// The unique <c>(SeriesId, OccurrenceDate)</c> index keeps this idempotent under races.
/// </para>
/// </summary>
public sealed class LessonMaterializer(
    ILessonRepository lessons,
    SeriesExpansion seriesExpansion,
    IStudentRepository students,
    TimeProvider clock,
    ILogger<LessonMaterializer> logger)
{
    /// <summary>
    /// The tutor's merged schedule intersecting <c>[fromUtc, toUtc)</c>: physical lessons plus
    /// virtual slots of active series that have no physical counterpart, ordered by start.
    /// </summary>
    public async Task<List<ScheduleSlot>> GetScheduleAsync(
        long tutorTelegramId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        Guid? studentId = null,
        CancellationToken ct = default)
    {
        var physical = await lessons.GetByTutorInRangeAsync(tutorTelegramId, fromUtc, toUtc, studentId, ct);
        var slots = physical.Select(ScheduleSlot.From).ToList();

        var free = await seriesExpansion.GetFreeOccurrencesAsync(tutorTelegramId, fromUtc, toUtc, studentId, ct);
        if (free.Count == 0)
            return slots.OrderBy(s => s.StartUtc).ToList();

        var rates = await ResolveRatesAsync(tutorTelegramId, free.Select(f => f.Series), ct);

        foreach (var (series, occurrences) in free)
        {
            // Data anomaly guard: a series whose student is missing from the bulk lookup
            // must not take the whole schedule down with a 500.
            var price = series.Price ?? rates.GetValueOrDefault(series.StudentId);
            slots.AddRange(occurrences.Select(o => ScheduleSlot.Virtual(series, o, price)));
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

        var created = Lesson.Create(
            series.TutorTelegramId,
            series.StudentId,
            occurrence.StartUtc,
            series.DurationMinutes,
            await ResolvePriceAsync(series, ct),
            clock.GetUtcNow(),
            seriesId: series.Id,
            occurrenceDate: occurrence.OccurrenceDate);
        if (!created.IsSuccess)
        {
            // The inputs come from a persisted series, not from the user — a failure means the
            // stored data violates lesson invariants. Surface it as the data anomaly it is
            // instead of quietly producing a broken row.
            var details = string.Join("; ", created.Errors.Select(e => e.Message));
            logger.LogError(
                "Materializing slot {OccurrenceDate} of series {SeriesId} produced an invalid lesson: {Errors}",
                occurrence.OccurrenceDate, series.Id, details);
            throw new InvalidOperationException(
                $"Series {series.Id} slot {occurrence.OccurrenceDate:yyyy-MM-dd} cannot materialize: {details}");
        }

        return created.Value;
    }

    /// <summary>Price snapshot: the series' own price, or the student's current rate.</summary>
    private async Task<decimal> ResolvePriceAsync(LessonSeries series, CancellationToken ct)
    {
        if (series.Price is { } price)
            return price;

        var student = await students.GetByIdAsync(series.StudentId, series.TutorTelegramId, ct: ct);
        if (student is not null)
            return student.Rate;

        // Same data anomaly guard as the read path above: a series whose student is gone
        // must not turn a slot mutation into an opaque 500 — snapshot a zero price instead.
        logger.LogWarning(
            "Student {StudentId} behind series {SeriesId} not found; materializing with price 0",
            series.StudentId, series.Id);
        return 0m;
    }

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
}
