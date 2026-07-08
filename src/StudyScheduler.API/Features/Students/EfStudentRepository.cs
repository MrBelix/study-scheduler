using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Features.Students;

/// <summary>EF Core implementation of <see cref="IStudentRepository"/> (SQL Server).</summary>
public sealed class EfStudentRepository(AppDbContext db) : IStudentRepository
{
    public async Task<Student?> GetByIdAsync(
        Guid id,
        long tutorTelegramId,
        bool track = false,
        CancellationToken ct = default)
    {
        var query = track ? db.Students : db.Students.AsNoTracking();
        return await query.SingleOrDefaultAsync(s => s.Id == id && s.TutorTelegramId == tutorTelegramId, ct);
    }

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

    public void Add(Student student) => db.Students.Add(student);

    public void Update(Student student) => db.Students.Update(student);
}
