using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Features.Students;

/// <summary>EF Core implementation of <see cref="IStudentRepository"/> (SQL Server).</summary>
public sealed class EfStudentRepository(AppDbContext db) : IStudentRepository
{
    public async Task<Student?> GetByIdAsync(Guid id) =>
        await db.Students.FindAsync(id);

    public async Task<List<Student>> GetAllByTutorIdAsync(long tutorTelegramId) =>
        await db.Students
            .Where(s => s.TutorTelegramId == tutorTelegramId)
            .ToListAsync();

    public async Task AddAsync(Student student)
    {
        db.Students.Add(student);
        await db.SaveChangesAsync();
    }

    public async Task UpdateAsync(Student student)
    {
        db.Students.Update(student);
        await db.SaveChangesAsync();
    }
}
