namespace StudyScheduler.Domain.Students;

/// <summary>
/// Persistence contract for <see cref="Student"/>. Lives in the domain so the API depends on the
/// abstraction; infrastructure (EF Core) provides the implementation. Every method reads the CURRENT
/// TENANT's students, so cross-tenant ids look exactly like missing ones.
/// </summary>
public interface IStudentRepository
{
    /// <summary>
    /// The student with the given id, or null when it is missing or another tutor's. Untracked
    /// unless <paramref name="track"/> is set; pass <c>true</c> when the entity will be mutated.
    /// </summary>
    Task<Student?> GetByIdAsync(Guid id, bool track = false, CancellationToken ct = default);

    /// <summary>
    /// Students with the given ids, in one round trip. Read-only (untracked) — do not mutate and save.
    /// </summary>
    Task<List<Student>> GetByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken ct = default);

    Task<List<Student>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// The tutor's students in one status. The status is part of the query so the list endpoints —
    /// which are split by status — never load the other half just to drop it.
    /// Read-only (untracked).
    /// </summary>
    Task<List<Student>> GetByStatusAsync(
        StudentStatus status,
        CancellationToken ct = default);

    /// <summary>
    /// Stages the student for insertion; nothing is written until
    /// <see cref="Primitives.IUnitOfWork.SaveChangesAsync"/>, which is also where the scope's tutor is
    /// stamped onto it.
    /// </summary>
    void Add(Student student);

    /// <summary>Stages the student for update; nothing is written until <see cref="Primitives.IUnitOfWork.SaveChangesAsync"/>.</summary>
    void Update(Student student);
}
