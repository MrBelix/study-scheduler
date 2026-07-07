using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Features.Students;

/// <summary>EF Core implementation of <see cref="IStudentRepository"/> (SQL Server).</summary>
public sealed class EfStudentRepository(AppDbContext db) : IStudentRepository
{
    public async Task<Student?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await db.Students.FindAsync([id], ct);

    public async Task<List<Student>> GetByIdsAsync(
        long tutorTelegramId,
        IReadOnlyCollection<Guid> ids,
        CancellationToken ct = default) =>
        await db.Students
            .AsNoTracking()
            .Where(s => s.TutorTelegramId == tutorTelegramId && ids.Contains(s.Id))
            .ToListAsync(ct);

    public async Task<List<Student>> GetAllByTutorIdAsync(
        long tutorTelegramId,
        CancellationToken ct = default) =>
        await db.Students
            .AsNoTracking()
            .Where(s => s.TutorTelegramId == tutorTelegramId)
            .ToListAsync(ct);

    public async Task AddAsync(Student student, CancellationToken ct = default)
    {
        db.Students.Add(student);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Student student, CancellationToken ct = default)
    {
        db.Students.Update(student);
        await db.SaveChangesAsync(ct);
    }
}
