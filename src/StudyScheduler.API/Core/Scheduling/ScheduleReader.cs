using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Core.Scheduling;

/// <summary>
/// The read half of virtual recurrence: merges the tutor's physical <see cref="Lesson"/> rows with
/// the virtual slots of active series that have no physical counterpart, into one ordered schedule.
/// A physical row always wins its slot (<see cref="SeriesExpansion"/> suppresses the taken virtual
/// slots). Nothing is written.
/// </summary>
public sealed class ScheduleReader(
    ILessonRepository lessons,
    SeriesExpansion seriesExpansion,
    IStudentRepository students)
{
    /// <summary>
    /// The tutor's merged schedule intersecting <c>[fromUtc, toUtc)</c>, ordered by start.
    /// </summary>
    public async Task<IReadOnlyList<ScheduleEntry>> GetScheduleAsync(
        long tutorTelegramId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        Guid? studentId = null,
        CancellationToken ct = default)
    {
        var physical = await lessons.GetByTutorInRangeAsync(tutorTelegramId, fromUtc, toUtc, studentId, ct);
        var entries = physical.Select(ScheduleEntry.From).ToList();

        var free = await seriesExpansion.GetFreeOccurrencesAsync(tutorTelegramId, fromUtc, toUtc, studentId, ct);
        if (free.Count == 0)
            return entries.OrderBy(e => e.StartUtc).ToList();

        var rates = await ResolveRatesAsync(tutorTelegramId, free.Select(f => f.Series), ct);
        foreach (var (series, occurrences) in free)
        {
            // Data anomaly guard: a series whose student is missing from the bulk lookup must not
            // take the whole schedule down — fall back to a zero price.
            var price = series.Price ?? rates.GetValueOrDefault(series.StudentId);
            entries.AddRange(occurrences.Select(o => ScheduleEntry.Virtual(series, o, price)));
        }

        return entries.OrderBy(e => e.StartUtc).ToList();
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
            return [];

        return (await students.GetByIdsAsync(tutorTelegramId, studentIds, ct))
            .ToDictionary(s => s.Id, s => s.Rate);
    }
}
