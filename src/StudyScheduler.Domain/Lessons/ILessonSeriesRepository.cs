namespace StudyScheduler.Domain.Lessons;

/// <summary>Persistence contract for <see cref="LessonSeries"/>. Scoped to the owning tutor.</summary>
public interface ILessonSeriesRepository
{
    Task<LessonSeries?> GetByIdAsync(Guid id, long tutorTelegramId, bool track = false, CancellationToken ct = default);

    /// <summary>
    /// The tutor's series that can still produce occurrences: open-ended, or ending on/after
    /// <paramref name="notEndedBefore"/>. A null cut-off returns every series (no date filter).
    /// Untracked — read-only consumers (expansion, overlap checks).
    /// </summary>
    Task<IReadOnlyList<LessonSeries>> GetActiveByTutorAsync(
        long tutorTelegramId,
        DateOnly? notEndedBefore = null,
        CancellationToken ct = default);

    /// <summary>All of the tutor's series (active and ended), oldest first — for the series list.</summary>
    Task<IReadOnlyList<LessonSeries>> GetAllByTutorAsync(long tutorTelegramId, CancellationToken ct = default);

    void Add(LessonSeries series);

    void Update(LessonSeries series);
}
