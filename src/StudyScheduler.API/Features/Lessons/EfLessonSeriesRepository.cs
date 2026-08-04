using Microsoft.EntityFrameworkCore;
using StudyScheduler.API.Core.Persistence;
using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>
/// EF Core implementation of <see cref="ILessonSeriesRepository"/> (PostgreSQL). Tenancy is
/// <see cref="AppDbContext"/>'s global query filter, not a predicate anyone writes here — except in
/// the one method whose name says it spans tutors.
/// </summary>
public sealed class EfLessonSeriesRepository(AppDbContext db) : ILessonSeriesRepository
{
    public async Task<LessonSeries?> GetByIdAsync(
        Guid id,
        bool track = false,
        CancellationToken ct = default)
    {
        var query = track ? db.LessonSeries : db.LessonSeries.AsNoTracking();
        return await query.SingleOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<IReadOnlyList<LessonSeries>> GetActiveAsync(
        DateOnly? notEndedBefore = null,
        CancellationToken ct = default) =>
        await db.LessonSeries
            .AsNoTracking()
            .Where(s => notEndedBefore == null || s.EndDate == null || s.EndDate >= notEndedBefore)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<LessonSeries>> GetAllAsync(CancellationToken ct = default) =>
        await db.LessonSeries
            .AsNoTracking()
            .OrderBy(s => s.CreatedAtUtc)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<LessonSeries>> GetStartedNotEndedAcrossAllTutorsAsync(
        DateOnly startedOnOrBefore,
        DateOnly notEndedBefore,
        CancellationToken ct = default) =>
        // IgnoreQueryFilters is the whole point of this method: the nightly pass has no tenant of its
        // own and must see every tutor's series. Ordered so a capped background pass walks the same
        // series in the same order every tick.
        await db.LessonSeries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.StartDate <= startedOnOrBefore
                && (s.EndDate == null || s.EndDate >= notEndedBefore))
            .OrderBy(s => s.CreatedAtUtc)
            .ToListAsync(ct);

    public void Add(LessonSeries series) => db.LessonSeries.Add(series);

    public void Update(LessonSeries series) => db.LessonSeries.Update(series);
}
