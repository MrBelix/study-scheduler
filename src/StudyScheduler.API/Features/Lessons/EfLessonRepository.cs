using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>EF Core implementation of <see cref="ILessonRepository"/> (SQL Server).</summary>
public sealed class EfLessonRepository(AppDbContext db) : ILessonRepository
{
    public async Task<Lesson?> GetByIdAsync(Guid id) =>
        await db.Lessons.FindAsync(id);

    public async Task<List<Lesson>> GetByTutorInRangeAsync(
        long tutorTelegramId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        Guid? studentId = null) =>
        await db.Lessons
            .Where(l => l.TutorTelegramId == tutorTelegramId
                && l.StartUtc < toUtc
                && l.EndUtc > fromUtc
                && (studentId == null || l.StudentId == studentId))
            .OrderBy(l => l.StartUtc)
            .ToListAsync();

    public async Task<List<Lesson>> GetOverlappingAsync(
        long tutorTelegramId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid? excludeLessonId = null) =>
        await db.Lessons
            .Where(l => l.TutorTelegramId == tutorTelegramId
                && l.Status != LessonStatus.Cancelled
                && l.StartUtc < endUtc
                && l.EndUtc > startUtc
                && (excludeLessonId == null || l.Id != excludeLessonId))
            .ToListAsync();

    public async Task<List<Lesson>> GetFromDateAsync(long tutorTelegramId, DateTimeOffset fromUtc) =>
        await db.Lessons
            .Where(l => l.TutorTelegramId == tutorTelegramId
                && l.Status != LessonStatus.Cancelled
                && l.StartUtc >= fromUtc)
            .ToListAsync();

    public async Task<List<DateOnly>> GetOccurrenceDatesAsync(Guid seriesId, DateOnly fromLocal, DateOnly toLocal) =>
        await db.Lessons
            .Where(l => l.SeriesId == seriesId
                && l.OccurrenceDate >= fromLocal
                && l.OccurrenceDate <= toLocal)
            .Select(l => l.OccurrenceDate!.Value)
            .ToListAsync();

    public async Task<List<Lesson>> GetBySeriesIdAsync(long tutorTelegramId, Guid seriesId) =>
        await db.Lessons
            .Where(l => l.TutorTelegramId == tutorTelegramId && l.SeriesId == seriesId)
            .OrderBy(l => l.StartUtc)
            .ToListAsync();

    public async Task AddAsync(Lesson lesson)
    {
        db.Lessons.Add(lesson);
        await db.SaveChangesAsync();
    }

    public async Task AddRangeAsync(IReadOnlyCollection<Lesson> lessons)
    {
        db.Lessons.AddRange(lessons);
        await db.SaveChangesAsync();
    }

    public async Task UpdateAsync(Lesson lesson)
    {
        db.Lessons.Update(lesson);
        await db.SaveChangesAsync();
    }

    public async Task UpdateRangeAsync(IReadOnlyCollection<Lesson> lessons)
    {
        db.Lessons.UpdateRange(lessons);
        await db.SaveChangesAsync();
    }
}
