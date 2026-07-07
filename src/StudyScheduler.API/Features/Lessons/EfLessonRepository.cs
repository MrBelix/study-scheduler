using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>EF Core implementation of <see cref="ILessonRepository"/> (SQL Server).</summary>
public sealed class EfLessonRepository(AppDbContext db) : ILessonRepository
{
    public async Task<Lesson?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await db.Lessons.FindAsync([id], ct);

    public async Task<List<Lesson>> GetByTutorInRangeAsync(
        long tutorTelegramId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        Guid? studentId = null,
        CancellationToken ct = default) =>
        await db.Lessons
            .AsNoTracking()
            .Where(l => l.TutorTelegramId == tutorTelegramId
                && l.StartUtc < toUtc
                && l.EndUtc > fromUtc
                && (studentId == null || l.StudentId == studentId))
            .OrderBy(l => l.StartUtc)
            .ToListAsync(ct);

    public async Task<List<Lesson>> GetOverlappingAsync(
        long tutorTelegramId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid? excludeLessonId = null,
        CancellationToken ct = default) =>
        await db.Lessons
            .AsNoTracking()
            .Where(l => l.TutorTelegramId == tutorTelegramId
                && l.Status != LessonStatus.Cancelled
                && l.StartUtc < endUtc
                && l.EndUtc > startUtc
                && (excludeLessonId == null || l.Id != excludeLessonId))
            .ToListAsync(ct);

    public async Task<List<Lesson>> GetFromDateAsync(
        long tutorTelegramId,
        DateTimeOffset fromUtc,
        CancellationToken ct = default) =>
        await db.Lessons
            .AsNoTracking()
            .Where(l => l.TutorTelegramId == tutorTelegramId
                && l.Status != LessonStatus.Cancelled
                && l.StartUtc >= fromUtc)
            .ToListAsync(ct);

    public async Task<List<(Guid SeriesId, DateOnly OccurrenceDate)>> GetOccurrenceDatesForSeriesAsync(
        IReadOnlyCollection<Guid> seriesIds,
        DateOnly fromLocal,
        DateOnly toLocal,
        CancellationToken ct = default)
    {
        var rows = await db.Lessons
            .AsNoTracking()
            .Where(l => l.SeriesId != null
                && seriesIds.Contains(l.SeriesId.Value)
                && l.OccurrenceDate >= fromLocal
                && l.OccurrenceDate <= toLocal)
            .Select(l => new { SeriesId = l.SeriesId!.Value, OccurrenceDate = l.OccurrenceDate!.Value })
            .ToListAsync(ct);

        return rows.Select(r => (r.SeriesId, r.OccurrenceDate)).ToList();
    }

    public async Task<Lesson?> GetBySeriesOccurrenceAsync(
        Guid seriesId,
        DateOnly occurrenceDate,
        CancellationToken ct = default) =>
        await db.Lessons
            .SingleOrDefaultAsync(l => l.SeriesId == seriesId && l.OccurrenceDate == occurrenceDate, ct);

    public async Task AddAsync(Lesson lesson, CancellationToken ct = default)
    {
        db.Lessons.Add(lesson);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Lesson lesson, CancellationToken ct = default)
    {
        db.Lessons.Update(lesson);
        await db.SaveChangesAsync(ct);
    }
}
