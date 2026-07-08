namespace StudyScheduler.Domain.Students;

/// <summary>
/// Persistence contract for <see cref="Student"/>. Lives in the domain so the API depends
/// on the abstraction; infrastructure (in-memory now, EF Core later) provides the implementation.
/// </summary>
public interface IStudentRepository
{
    /// <summary>
    /// The student with the given id owned by the tutor, or null — ownership is part of the
    /// query, so cross-tenant ids look exactly like missing ones. Untracked unless
    /// <paramref name="track"/> is set; pass <c>true</c> when the entity will be mutated.
    /// </summary>
    Task<Student?> GetByIdAsync(Guid id, long tutorTelegramId, bool track = false, CancellationToken ct = default);

    /// <summary>
    /// Students with the given ids, scoped to the tutor, in one round trip.
    /// Read-only (untracked) — do not mutate and save.
    /// </summary>
    Task<List<Student>> GetByIdsAsync(
        long tutorTelegramId,
        IReadOnlyCollection<Guid> ids,
        CancellationToken ct = default);

    Task<List<Student>> GetAllByTutorIdAsync(long tutorTelegramId, CancellationToken ct = default);

    /// <summary>Stages the student for insertion; nothing is written until <see cref="Primitives.IUnitOfWork.SaveChangesAsync"/>.</summary>
    void Add(Student student);

    /// <summary>Stages the student for update; nothing is written until <see cref="Primitives.IUnitOfWork.SaveChangesAsync"/>.</summary>
    void Update(Student student);
}
