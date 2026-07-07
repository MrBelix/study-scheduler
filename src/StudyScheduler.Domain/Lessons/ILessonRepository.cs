namespace StudyScheduler.Domain.Lessons;

/// <summary>
/// Persistence contract for <see cref="Lesson"/>. Lives in the domain so the API depends on the
/// abstraction; infrastructure (EF Core) provides the implementation.
/// </summary>
public interface ILessonRepository
{
    Task<Lesson?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Lessons of the tutor intersecting <c>[fromUtc, toUtc)</c>, ordered by start.</summary>
    Task<List<Lesson>> GetByTutorInRangeAsync(
        long tutorTelegramId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        Guid? studentId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Non-cancelled lessons of the tutor overlapping <c>(startUtc, endUtc)</c> — strict
    /// inequalities, so back-to-back lessons do not conflict.
    /// </summary>
    Task<List<Lesson>> GetOverlappingAsync(
        long tutorTelegramId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid? excludeLessonId = null,
        CancellationToken ct = default);

    /// <summary>Non-cancelled lessons of the tutor starting at or after <paramref name="fromUtc"/>.</summary>
    Task<List<Lesson>> GetFromDateAsync(
        long tutorTelegramId,
        DateTimeOffset fromUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Occurrence dates already materialized within the local date range, for many series in one
    /// round trip (avoids the per-series N+1 when expanding schedules).
    /// </summary>
    Task<List<(Guid SeriesId, DateOnly OccurrenceDate)>> GetOccurrenceDatesForSeriesAsync(
        IReadOnlyCollection<Guid> seriesIds,
        DateOnly fromLocal,
        DateOnly toLocal,
        CancellationToken ct = default);

    /// <summary>The physical lesson materialized for a specific series slot, if any.</summary>
    Task<Lesson?> GetBySeriesOccurrenceAsync(
        Guid seriesId,
        DateOnly occurrenceDate,
        CancellationToken ct = default);

    Task AddAsync(Lesson lesson, CancellationToken ct = default);

    Task UpdateAsync(Lesson lesson, CancellationToken ct = default);
}
