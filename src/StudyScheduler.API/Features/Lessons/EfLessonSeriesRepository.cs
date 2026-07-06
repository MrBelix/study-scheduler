using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>EF Core implementation of <see cref="ILessonSeriesRepository"/> (SQL Server).</summary>
public sealed class EfLessonSeriesRepository(AppDbContext db) : ILessonSeriesRepository
{
    public async Task<LessonSeries?> GetByIdAsync(Guid id) =>
        await db.LessonSeries.FindAsync(id);

    public async Task<List<LessonSeries>> GetActiveByTutorAsync(long tutorTelegramId) =>
        await db.LessonSeries
            .Where(s => s.TutorTelegramId == tutorTelegramId && s.IsActive)
            .ToListAsync();

    public async Task<List<LessonSeries>> GetAllByTutorAsync(long tutorTelegramId) =>
        await db.LessonSeries
            .Where(s => s.TutorTelegramId == tutorTelegramId)
            .OrderBy(s => s.CreatedAtUtc)
            .ToListAsync();

    public async Task AddAsync(LessonSeries series)
    {
        db.LessonSeries.Add(series);
        await db.SaveChangesAsync();
    }

    public async Task UpdateAsync(LessonSeries series)
    {
        db.LessonSeries.Update(series);
        await db.SaveChangesAsync();
    }
}
