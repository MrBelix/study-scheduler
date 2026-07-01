namespace StudyScheduler.Domain.Students;

/// <summary>
/// Persistence contract for <see cref="Student"/>. Lives in the domain so the API depends
/// on the abstraction; infrastructure (in-memory now, EF Core later) provides the implementation.
/// </summary>
public interface IStudentRepository
{
    Task<Student?> GetByIdAsync(Guid id);

    Task<List<Student>> GetAllByTutorIdAsync(long tutorTelegramId);

    Task AddAsync(Student student);

    Task UpdateAsync(Student student);
}
