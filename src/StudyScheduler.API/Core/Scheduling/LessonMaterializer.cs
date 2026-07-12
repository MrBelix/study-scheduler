using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Core.Scheduling;

/// <summary>
/// Turns one virtual series slot into a physical <see cref="Lesson"/> the moment it is first
/// modified (topic/description, cancel, reschedule). The caller applies the mutation that
/// triggered materialization and persists; the unique <c>(SeriesId, OccurrenceDate)</c> index
/// keeps it idempotent under races.
/// </summary>
public sealed class LessonMaterializer(
    IStudentRepository students,
    TimeProvider clock,
    ILogger<LessonMaterializer> logger)
{
    /// <summary>
    /// Instantiates (without saving) the physical <see cref="Lesson"/> for one slot, carrying the
    /// series link and a price snapshot. Duration comes from the occurrence itself, so a slot
    /// always materializes at the exact time it was expanded.
    /// </summary>
    public async Task<Lesson> MaterializeSlotAsync(
        LessonSeries series,
        LessonOccurrence occurrence,
        CancellationToken ct = default)
    {
        var durationMinutes = (int)(occurrence.EndUtc - occurrence.StartUtc).TotalMinutes;

        var created = Lesson.Create(
            series.TutorTelegramId,
            series.StudentId,
            occurrence.StartUtc,
            durationMinutes,
            await ResolvePriceAsync(series, ct),
            clock.GetUtcNow(),
            seriesId: series.Id,
            occurrenceDate: occurrence.OccurrenceDate);
        if (!created.IsSuccess)
        {
            // The inputs come from a persisted series, not the user — a failure means the stored
            // data violates lesson invariants. Surface it as the data anomaly it is.
            var details = string.Join("; ", created.Errors.Select(e => e.Message));
            logger.LogError(
                "Materializing slot {OccurrenceDate} of series {SeriesId} produced an invalid lesson: {Errors}",
                occurrence.OccurrenceDate, series.Id, details);
            throw new InvalidOperationException(
                $"Series {series.Id} slot {occurrence.OccurrenceDate:yyyy-MM-dd} cannot materialize: {details}");
        }

        return created.Value;
    }

    /// <summary>Price snapshot: the series' own price, or the student's current rate (0 if gone).</summary>
    private async Task<decimal> ResolvePriceAsync(LessonSeries series, CancellationToken ct)
    {
        if (series.Price is { } price)
            return price;

        var student = await students.GetByIdAsync(series.StudentId, series.TutorTelegramId, ct: ct);
        if (student is not null)
            return student.Rate;

        // Data anomaly guard: a series whose student is gone must not turn a slot mutation into an
        // opaque 500 — snapshot a zero price instead.
        logger.LogWarning(
            "Student {StudentId} behind series {SeriesId} not found; materializing with price 0",
            series.StudentId, series.Id);
        return 0m;
    }
}
