using Microsoft.EntityFrameworkCore;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>
/// Lazily materializes lessons from active series for a requested UTC range: computes the
/// occurrences, skips the ones already stored (by canonical <c>OccurrenceDate</c>) and inserts the
/// rest. Called on every lesson-range read, so open-ended series never need a background job.
/// </summary>
public sealed class LessonMaterializer(
    ILessonRepository lessons,
    ILessonSeriesRepository seriesRepo,
    IStudentRepository students,
    TimeProvider clock)
{
    public async Task MaterializeAsync(long tutorTelegramId, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var activeSeries = await seriesRepo.GetActiveByTutorAsync(tutorTelegramId);
        if (activeSeries.Count == 0)
            return;

        // ±1 day of local-date slack around the UTC range covers any time-zone offset.
        var fromLocal = DateOnly.FromDateTime(fromUtc.UtcDateTime).AddDays(-1);
        var toLocal = DateOnly.FromDateTime(toUtc.UtcDateTime).AddDays(1);

        var toCreate = new List<Lesson>();
        foreach (var series in activeSeries)
        {
            var missing = series.GetOccurrences(fromLocal, toLocal)
                .Where(o => o.StartUtc < toUtc && o.EndUtc > fromUtc)
                .ToList();
            if (missing.Count == 0)
                continue;

            var existing = (await lessons.GetOccurrenceDatesAsync(series.Id, fromLocal, toLocal)).ToHashSet();
            missing.RemoveAll(o => existing.Contains(o.OccurrenceDate));
            if (missing.Count == 0)
                continue;

            // Price snapshot: the series' own price, or the student's current rate.
            var price = series.Price ?? (await students.GetByIdAsync(series.StudentId))!.Rate;

            toCreate.AddRange(missing.Select(o => Lesson.Create(
                tutorTelegramId,
                series.StudentId,
                o.StartUtc,
                series.DurationMinutes,
                price,
                clock.GetUtcNow(),
                seriesId: series.Id,
                occurrenceDate: o.OccurrenceDate)));
        }

        if (toCreate.Count == 0)
            return;

        try
        {
            await lessons.AddRangeAsync(toCreate);
        }
        catch (DbUpdateException)
        {
            // A concurrent read materialized (some of) the same occurrences first — the unique
            // (SeriesId, OccurrenceDate) index rejected our batch. Those rows exist, so the
            // caller's subsequent range query still returns them; whatever else was in our batch
            // gets inserted by the next read.
        }
    }
}
