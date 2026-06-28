namespace StudyScheduler.Domain.Students;

/// <summary>
/// Persistence contract for <see cref="Student"/>. Lives in the domain so the API depends
/// on the abstraction; infrastructure (in-memory now, EF Core later) provides the implementation.
/// </summary>
public interface IStudentRepository
{
    Task<Student?> GetByIdAsync(Guid id);

    Task<List<Student>> GetAllByOwnerIdAsync(long ownerTelegramId);

    Task AddAsync(Student student);
}
