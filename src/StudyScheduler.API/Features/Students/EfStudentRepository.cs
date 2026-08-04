using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Features.Students;

/// <summary>
/// EF Core implementation of <see cref="IStudentRepository"/> (PostgreSQL). Tenancy is
/// <see cref="AppDbContext"/>'s global query filter, not a predicate anyone writes here.
/// </summary>
public sealed class EfStudentRepository(AppDbContext db) : IStudentRepository
{
    public async Task<Student?> GetByIdAsync(
        Guid id,
        bool track = false,
        CancellationToken ct = default)
    {
        var query = track ? db.Students : db.Students.AsNoTracking();
        return await query.SingleOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<List<Student>> GetByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken ct = default) =>
        await db.Students
            .AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .ToListAsync(ct);

    public async Task<List<Student>> GetAllAsync(CancellationToken ct = default) =>
        await db.Students
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<List<Student>> GetByStatusAsync(
        StudentStatus status,
        CancellationToken ct = default) =>
        await db.Students
            .AsNoTracking()
            .Where(s => s.Status == status)
            .ToListAsync(ct);

    public void Add(Student student) => db.Students.Add(student);

    public void Update(Student student) => db.Students.Update(student);
}
