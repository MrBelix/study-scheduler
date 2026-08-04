using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>
/// EF Core implementation of <see cref="ILessonRepository"/> (PostgreSQL). Not one predicate here
/// mentions the tutor: <see cref="AppDbContext"/>'s global query filter narrows every query below to
/// the scope's tenant, and the same tenant is stamped onto whatever <see cref="Add"/> stages.
/// </summary>
public sealed class EfLessonRepository(AppDbContext db) : ILessonRepository
{
    public async Task<Lesson?> GetByIdAsync(
        Guid id,
        bool track = false,
        CancellationToken ct = default)
    {
        var query = track ? db.Lessons : db.Lessons.AsNoTracking();
        return await query.SingleOrDefaultAsync(l => l.Id == id, ct);
    }

    public async Task<IReadOnlyList<Lesson>> GetByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        bool track = false,
        CancellationToken ct = default)
    {
        var query = track ? db.Lessons : db.Lessons.AsNoTracking();
        return await query
            .Where(l => ids.Contains(l.Id))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Lesson>> GetInRangeAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        Guid? studentId = null,
        CancellationToken ct = default) =>
        await db.Lessons
            .AsNoTracking()
            .Where(l => l.StartUtc < toUtc
                && l.EndUtc > fromUtc
                && (studentId == null || l.StudentId == studentId))
            .OrderBy(l => l.StartUtc)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Lesson>> GetOverlappingAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid? excludeLessonId = null,
        CancellationToken ct = default) =>
        await db.Lessons
            .AsNoTracking()
            .Where(l => l.Status != LessonStatus.Cancelled
                && l.StartUtc < endUtc
                && l.EndUtc > startUtc
                && (excludeLessonId == null || l.Id != excludeLessonId))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Lesson>> GetFromDateAsync(
        DateTimeOffset fromUtc,
        CancellationToken ct = default) =>
        await db.Lessons
            .AsNoTracking()
            .Where(l => l.Status != LessonStatus.Cancelled && l.StartUtc >= fromUtc)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<SeriesSlot>> GetMaterializedSlotsAsync(
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

        return rows.Select(r => new SeriesSlot(r.SeriesId, r.OccurrenceDate)).ToList();
    }

    public async Task<StudentLessonStats> GetStudentStatsAsync(
        Guid studentId,
        CancellationToken ct = default)
    {
        var mine = db.Lessons
            .AsNoTracking()
            .Where(l => l.StudentId == studentId);

        // Counting and summing happens server-side; no rows travel. A student without lessons
        // produces no group at all, which is already the "nothing yet" answer.
        var totals = await mine
            .GroupBy(_ => 1)
            .Select(g => new
            {
                CompletedCount = g.Count(l => l.Status == LessonStatus.Completed),
                // Mirrors the reports' actual income: only non-cancelled, paid lessons were received.
                MoneyReceived = g.Sum(l => l.Status != LessonStatus.Cancelled && l.IsPaid ? l.Price : 0m),
            })
            .SingleOrDefaultAsync(ct);

        // Separate ORDER BY + LIMIT 1 rather than MIN(): StartUtc goes through a value converter,
        // and this form rides the (TutorTelegramId, StartUtc) index anyway.
        var earliest = await mine
            .OrderBy(l => l.StartUtc)
            .Select(l => l.StartUtc)
            .Take(1)
            .ToListAsync(ct);

        return new StudentLessonStats(
            totals?.CompletedCount ?? 0,
            totals?.MoneyReceived ?? 0m,
            earliest.Count == 0 ? null : earliest[0]);
    }

    public async Task<IReadOnlyList<Lesson>> GetDebtForStudentAsync(
        Guid studentId,
        CancellationToken ct = default) =>
        // The debt definition itself is the domain's — handed straight to the database, so this
        // student's banner and the Money screen's ledger select the very same rows.
        await db.Lessons
            .AsNoTracking()
            .Where(Lesson.IsDebt)
            .Where(l => l.StudentId == studentId)
            .OrderByDescending(l => l.StartUtc)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Lesson>> GetFutureForSeriesAsync(
        Guid seriesId,
        DateTimeOffset afterUtc,
        bool track = false,
        CancellationToken ct = default)
    {
        var query = track ? db.Lessons : db.Lessons.AsNoTracking();
        return await query
            .Where(l => l.SeriesId == seriesId && l.StartUtc > afterUtc)
            .OrderBy(l => l.StartUtc)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Lesson>> GetFutureForStudentAsync(
        Guid studentId,
        DateTimeOffset afterUtc,
        bool track = false,
        CancellationToken ct = default)
    {
        var query = track ? db.Lessons : db.Lessons.AsNoTracking();
        return await query
            .Where(l => l.StudentId == studentId && l.StartUtc > afterUtc)
            .OrderBy(l => l.StartUtc)
            .ToListAsync(ct);
    }

    public void Add(Lesson lesson) => db.Lessons.Add(lesson);

    public void Update(Lesson lesson) => db.Lessons.Update(lesson);

    public void Remove(Lesson lesson) => db.Lessons.Remove(lesson);
}
