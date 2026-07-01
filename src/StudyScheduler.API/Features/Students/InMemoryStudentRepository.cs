using System.Collections.Concurrent;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Features.Students;

/// <summary>
/// In-memory store for early development. Registered as a singleton so data survives
/// across requests. Swap for an EF Core implementation without touching the endpoints.
/// </summary>
public sealed class InMemoryStudentRepository : IStudentRepository
{
    private readonly ConcurrentDictionary<Guid, Student> _students = new();

    public Task<Student?> GetByIdAsync(Guid id) =>
        Task.FromResult(_students.GetValueOrDefault(id));

    public Task<List<Student>> GetAllByTutorIdAsync(long tutorTelegramId) =>
        Task.FromResult(_students.Values
            .Where(s => s.TutorTelegramId == tutorTelegramId)
            .ToList());

    public Task AddAsync(Student student)
    {
        _students[student.Id] = student;
        return Task.CompletedTask;
    }

    // Entities are stored by reference, so mutations are already visible. Kept for
    // parity with the EF Core implementation, where this maps to SaveChanges.
    public Task UpdateAsync(Student student)
    {
        _students[student.Id] = student;
        return Task.CompletedTask;
    }
}
