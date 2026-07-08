namespace StudyScheduler.Domain.Lessons;

/// <summary>Persistence contract for <see cref="LessonSeries"/>.</summary>
public interface ILessonSeriesRepository
{
    /// <summary>
    /// The series with the given id owned by the tutor, or null — ownership is part of the
    /// query, so cross-tenant ids look exactly like missing ones. Untracked unless
    /// <paramref name="track"/> is set; pass <c>true</c> when the entity will be mutated.
    /// </summary>
    Task<LessonSeries?> GetByIdAsync(Guid id, long tutorTelegramId, bool track = false, CancellationToken ct = default);

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

    /// <summary>Stages the series for insertion; nothing is written until <see cref="Primitives.IUnitOfWork.SaveChangesAsync"/>.</summary>
    void Add(LessonSeries series);

    /// <summary>Stages the series for update; nothing is written until <see cref="Primitives.IUnitOfWork.SaveChangesAsync"/>.</summary>
    void Update(LessonSeries series);
}
