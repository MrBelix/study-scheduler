namespace StudyScheduler.Domain.Lessons;

/// <summary>
/// Persistence contract for <see cref="Lesson"/>. Lives in the domain so the API depends on the
/// abstraction; infrastructure (EF Core) provides the implementation.
/// </summary>
public interface ILessonRepository
{
    Task<Lesson?> GetByIdAsync(Guid id);

    /// <summary>Lessons of the tutor intersecting <c>[fromUtc, toUtc)</c>, ordered by start.</summary>
    Task<List<Lesson>> GetByTutorInRangeAsync(
        long tutorTelegramId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        Guid? studentId = null);

    /// <summary>
    /// Non-cancelled lessons of the tutor overlapping <c>(startUtc, endUtc)</c> — strict
    /// inequalities, so back-to-back lessons do not conflict.
    /// </summary>
    Task<List<Lesson>> GetOverlappingAsync(
        long tutorTelegramId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid? excludeLessonId = null);

    /// <summary>Non-cancelled lessons of the tutor starting at or after <paramref name="fromUtc"/>.</summary>
    Task<List<Lesson>> GetFromDateAsync(long tutorTelegramId, DateTimeOffset fromUtc);

    /// <summary>Occurrence dates of a series already materialized within the local date range.</summary>
    Task<List<DateOnly>> GetOccurrenceDatesAsync(Guid seriesId, DateOnly fromLocal, DateOnly toLocal);

    Task<List<Lesson>> GetBySeriesIdAsync(long tutorTelegramId, Guid seriesId);

    Task AddAsync(Lesson lesson);

    /// <summary>Adds the batch atomically (single SaveChanges).</summary>
    Task AddRangeAsync(IReadOnlyCollection<Lesson> lessons);

    Task UpdateAsync(Lesson lesson);

    Task UpdateRangeAsync(IReadOnlyCollection<Lesson> lessons);
}
