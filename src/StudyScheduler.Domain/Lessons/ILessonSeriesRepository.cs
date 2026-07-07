namespace StudyScheduler.Domain.Lessons;

/// <summary>Persistence contract for <see cref="LessonSeries"/>.</summary>
public interface ILessonSeriesRepository
{
    Task<LessonSeries?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Active series of the tutor. Read-only (untracked) — do not mutate and save.</summary>
    Task<List<LessonSeries>> GetActiveByTutorAsync(long tutorTelegramId, CancellationToken ct = default);

    /// <summary>Active series of one student, scoped to the tutor. Tracked — safe to mutate and save.</summary>
    Task<List<LessonSeries>> GetActiveByStudentAsync(
        long tutorTelegramId,
        Guid studentId,
        CancellationToken ct = default);

    /// <summary>All series of the tutor. Read-only (untracked) — do not mutate and save.</summary>
    Task<List<LessonSeries>> GetAllByTutorAsync(long tutorTelegramId, CancellationToken ct = default);

    /// <summary>
    /// Active series of the tutor anchored in <paramref name="timeZone"/>. Tracked — the
    /// profile-zone-change flow mutates and saves these entities.
    /// </summary>
    Task<List<LessonSeries>> GetActiveByTimeZoneAsync(
        long tutorTelegramId,
        TimeZoneInfo timeZone,
        CancellationToken ct = default);

    Task AddAsync(LessonSeries series, CancellationToken ct = default);

    Task UpdateAsync(LessonSeries series, CancellationToken ct = default);
}
