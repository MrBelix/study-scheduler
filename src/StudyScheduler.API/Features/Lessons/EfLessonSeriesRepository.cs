using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>EF Core implementation of <see cref="ILessonSeriesRepository"/> (SQL Server).</summary>
public sealed class EfLessonSeriesRepository(AppDbContext db) : ILessonSeriesRepository
{
    public async Task<LessonSeries?> GetByIdAsync(
        Guid id,
        long tutorTelegramId,
        bool track = false,
        CancellationToken ct = default)
    {
        var query = track ? db.LessonSeries : db.LessonSeries.AsNoTracking();
        return await query.SingleOrDefaultAsync(s => s.Id == id && s.TutorTelegramId == tutorTelegramId, ct);
    }

    public async Task<IReadOnlyList<LessonSeries>> GetActiveByTutorAsync(
        long tutorTelegramId,
        DateOnly? notEndedBefore = null,
        CancellationToken ct = default) =>
        await db.LessonSeries
            .AsNoTracking()
            .Where(s => s.TutorTelegramId == tutorTelegramId
                && (notEndedBefore == null || s.EndDate == null || s.EndDate >= notEndedBefore))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<LessonSeries>> GetAllByTutorAsync(
        long tutorTelegramId,
        CancellationToken ct = default) =>
        await db.LessonSeries
            .AsNoTracking()
            .Where(s => s.TutorTelegramId == tutorTelegramId)
            .OrderBy(s => s.CreatedAtUtc)
            .ToListAsync(ct);

    public void Add(LessonSeries series) => db.LessonSeries.Add(series);

    public void Update(LessonSeries series) => db.LessonSeries.Update(series);
}
