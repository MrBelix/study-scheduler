namespace StudyScheduler.Domain.Lessons;

/// <summary>
/// Persistence contract for <see cref="Lesson"/>. Lives in the domain so the API depends on the
/// abstraction; infrastructure (EF Core) provides the implementation. Ownership is part of every
/// query, so a cross-tenant id reads exactly like a missing one.
/// </summary>
public interface ILessonRepository
{
    /// <summary>The lesson owned by the tutor, or null. Untracked unless <paramref name="track"/>.</summary>
    Task<Lesson?> GetByIdAsync(Guid id, long tutorTelegramId, bool track = false, CancellationToken ct = default);

    /// <summary>
    /// Lessons of the tutor intersecting <c>[fromUtc, toUtc)</c>, ordered by start. Includes every
    /// status (a cancelled lesson still shows on the schedule).
    /// </summary>
    Task<IReadOnlyList<Lesson>> GetByTutorInRangeAsync(
        long tutorTelegramId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        Guid? studentId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Non-cancelled lessons of the tutor overlapping <c>(startUtc, endUtc)</c> — strict
    /// inequalities, so back-to-back lessons do not conflict.
    /// </summary>
    Task<IReadOnlyList<Lesson>> GetOverlappingAsync(
        long tutorTelegramId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid? excludeLessonId = null,
        CancellationToken ct = default);

    /// <summary>Non-cancelled lessons of the tutor starting at or after <paramref name="fromUtc"/>.</summary>
    Task<IReadOnlyList<Lesson>> GetFromDateAsync(
        long tutorTelegramId,
        DateTimeOffset fromUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Slots already materialized within the local date range, for many series in one round trip
    /// (avoids the per-series N+1 when suppressing taken occurrences during expansion).
    /// </summary>
    Task<IReadOnlyList<SeriesSlot>> GetMaterializedSlotsAsync(
        IReadOnlyCollection<Guid> seriesIds,
        DateOnly fromLocal,
        DateOnly toLocal,
        CancellationToken ct = default);

    /// <summary>The physical lesson materialized for a specific series slot, if any (tutor-scoped).</summary>
    Task<Lesson?> GetBySeriesOccurrenceAsync(
        Guid seriesId,
        DateOnly occurrenceDate,
        long tutorTelegramId,
        bool track = false,
        CancellationToken ct = default);

    /// <summary>
    /// Materialized rows of the series with <c>OccurrenceDate &gt;= fromOccurrenceDate</c> — the
    /// future overrides to clean up when the series is ended.
    /// </summary>
    Task<IReadOnlyList<Lesson>> GetMaterializedForSeriesFromAsync(
        Guid seriesId,
        long tutorTelegramId,
        DateOnly fromOccurrenceDate,
        bool track = false,
        CancellationToken ct = default);

    void Add(Lesson lesson);

    void Update(Lesson lesson);

    void Remove(Lesson lesson);
}
