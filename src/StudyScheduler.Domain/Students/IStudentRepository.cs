namespace StudyScheduler.Domain.Students;

/// <summary>
/// Persistence contract for <see cref="Student"/>. Lives in the domain so the API depends
/// on the abstraction; infrastructure (in-memory now, EF Core later) provides the implementation.
/// </summary>
public interface IStudentRepository
{
    Task<Student?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Students with the given ids, scoped to the tutor, in one round trip.
    /// Read-only (untracked) — do not mutate and save.
    /// </summary>
    Task<List<Student>> GetByIdsAsync(
        long tutorTelegramId,
        IReadOnlyCollection<Guid> ids,
        CancellationToken ct = default);

    Task<List<Student>> GetAllByTutorIdAsync(long tutorTelegramId, CancellationToken ct = default);

    Task AddAsync(Student student, CancellationToken ct = default);

    Task UpdateAsync(Student student, CancellationToken ct = default);
}
