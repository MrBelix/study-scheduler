using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>EF Core implementation of <see cref="ILessonSeriesRepository"/> (SQL Server).</summary>
public sealed class EfLessonSeriesRepository(AppDbContext db) : ILessonSeriesRepository
{
    public async Task<LessonSeries?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await db.LessonSeries.FindAsync([id], ct);

    // Read-only consumers (schedule expansion, overlap checks) — untracked per the contract.
    public async Task<List<LessonSeries>> GetActiveByTutorAsync(
        long tutorTelegramId,
        CancellationToken ct = default) =>
        await db.LessonSeries
            .AsNoTracking()
            .Where(s => s.TutorTelegramId == tutorTelegramId && s.IsActive)
            .ToListAsync(ct);

    // Tracked: the student-archiving flow mutates and saves these entities.
    public async Task<List<LessonSeries>> GetActiveByStudentAsync(
        long tutorTelegramId,
        Guid studentId,
        CancellationToken ct = default) =>
        await db.LessonSeries
            .Where(s => s.TutorTelegramId == tutorTelegramId && s.StudentId == studentId && s.IsActive)
            .ToListAsync(ct);

    public async Task<List<LessonSeries>> GetAllByTutorAsync(
        long tutorTelegramId,
        CancellationToken ct = default) =>
        await db.LessonSeries
            .AsNoTracking()
            .Where(s => s.TutorTelegramId == tutorTelegramId)
            .OrderBy(s => s.CreatedAtUtc)
            .ToListAsync(ct);

    // Tracked: the profile-zone-change flow mutates and saves these entities. The equality
    // comparison translates through the TimeZoneInfo→string value converter to the stored id.
    public async Task<List<LessonSeries>> GetActiveByTimeZoneAsync(
        long tutorTelegramId,
        TimeZoneInfo timeZone,
        CancellationToken ct = default) =>
        await db.LessonSeries
            .Where(s => s.TutorTelegramId == tutorTelegramId && s.IsActive && s.TimeZone == timeZone)
            .ToListAsync(ct);

    public async Task AddAsync(LessonSeries series, CancellationToken ct = default)
    {
        db.LessonSeries.Add(series);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(LessonSeries series, CancellationToken ct = default)
    {
        db.LessonSeries.Update(series);
        await db.SaveChangesAsync(ct);
    }
}
