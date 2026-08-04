using StudyScheduler.API.Core.Tenancy;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Primitives;
using StudyScheduler.Tests.Core.Tenancy;

namespace StudyScheduler.Tests.Features.Lessons;

/// <summary>
/// In-memory <see cref="ILessonRepository"/> mirroring the EF semantics — including tenancy: every
/// read below is narrowed to <see cref="ITutorContext.CurrentTutorTelegramId"/> exactly as
/// <c>AppDbContext</c>'s global query filter narrows the real ones (a scope with no tenant reads
/// nothing), and <see cref="Add"/> stamps that tenant onto the row exactly as its
/// <c>SaveChanges</c> does. Fixtures seed through <see cref="Add"/>, or through
/// <see cref="TenantOwnership.OwnedBy"/> when the row must belong to another tutor.
/// </summary>
internal sealed class FakeLessonRepository(ITutorContext tutor) : ILessonRepository
{
    /// <summary>Every stored row, of every tenant — the assertion surface, not the query surface.</summary>
    public List<Lesson> Items { get; } = [];

    /// <summary>The rows the current scope can see. Telegram ids are positive, so no tenant sees none.</summary>
    public IEnumerable<Lesson> Mine =>
        Items.Where(l => l.TutorTelegramId == (tutor.CurrentTutorTelegramId ?? 0));

    public Task<Lesson?> GetByIdAsync(Guid id, bool track = false, CancellationToken ct = default) =>
        Task.FromResult(Mine.SingleOrDefault(l => l.Id == id));

    public Task<IReadOnlyList<Lesson>> GetByIdsAsync(
        IReadOnlyCollection<Guid> ids, bool track = false, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Lesson>>(Mine
            .Where(l => ids.Contains(l.Id))
            .ToList());

    public Task<IReadOnlyList<Lesson>> GetInRangeAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc,
        Guid? studentId = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Lesson>>(Mine
            .Where(l => l.StartUtc < toUtc
                && l.EndUtc > fromUtc
                && (studentId == null || l.StudentId == studentId))
            .OrderBy(l => l.StartUtc)
            .ToList());

    public Task<IReadOnlyList<Lesson>> GetOverlappingAsync(
        DateTimeOffset startUtc, DateTimeOffset endUtc,
        Guid? excludeLessonId = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Lesson>>(Mine
            .Where(l => l.Status != LessonStatus.Cancelled
                && l.StartUtc < endUtc
                && l.EndUtc > startUtc
                && (excludeLessonId == null || l.Id != excludeLessonId))
            .ToList());

    public Task<IReadOnlyList<Lesson>> GetFromDateAsync(
        DateTimeOffset fromUtc, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Lesson>>(Mine
            .Where(l => l.Status != LessonStatus.Cancelled && l.StartUtc >= fromUtc)
            .ToList());

    public Task<IReadOnlyList<SeriesSlot>> GetMaterializedSlotsAsync(
        IReadOnlyCollection<Guid> seriesIds, DateOnly fromLocal, DateOnly toLocal, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SeriesSlot>>(Mine
            .Where(l => l.SeriesId is { } sid && seriesIds.Contains(sid)
                && l.OccurrenceDate is { } d && d >= fromLocal && d <= toLocal)
            .Select(l => new SeriesSlot(l.SeriesId!.Value, l.OccurrenceDate!.Value))
            .ToList());

    public Task<StudentLessonStats> GetStudentStatsAsync(Guid studentId, CancellationToken ct = default)
    {
        var mine = Mine.Where(l => l.StudentId == studentId).ToList();

        return Task.FromResult(new StudentLessonStats(
            mine.Count(l => l.Status == LessonStatus.Completed),
            mine.Where(l => l.Status != LessonStatus.Cancelled && l.IsPaid).Sum(l => l.Price),
            mine.Count == 0 ? null : mine.Min(l => l.StartUtc)));
    }

    public Task<IReadOnlyList<Lesson>> GetDebtForStudentAsync(
        Guid studentId, CancellationToken ct = default) =>
        // The domain's own debt predicate, exactly as the EF query hands it to the database.
        Task.FromResult<IReadOnlyList<Lesson>>(Mine
            .AsQueryable()
            .Where(Lesson.IsDebt)
            .Where(l => l.StudentId == studentId)
            .OrderByDescending(l => l.StartUtc)
            .ToList());

    public Task<IReadOnlyList<Lesson>> GetFutureForSeriesAsync(
        Guid seriesId, DateTimeOffset afterUtc, bool track = false, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Lesson>>(Mine
            .Where(l => l.SeriesId == seriesId && l.StartUtc > afterUtc)
            .OrderBy(l => l.StartUtc)
            .ToList());

    public Task<IReadOnlyList<Lesson>> GetFutureForStudentAsync(
        Guid studentId, DateTimeOffset afterUtc, bool track = false, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Lesson>>(Mine
            .Where(l => l.StudentId == studentId && l.StartUtc > afterUtc)
            .OrderBy(l => l.StartUtc)
            .ToList());

    public void Add(Lesson lesson) => Items.Add(TenantStamp.Apply(lesson, tutor));

    public void Update(Lesson lesson) { }

    public void Remove(Lesson lesson) => Items.Remove(lesson);
}

/// <summary>
/// In-memory <see cref="ILessonSeriesRepository"/> mirroring the EF semantics, tenancy included —
/// see <see cref="FakeLessonRepository"/>. The one deliberately cross-tenant read is the only method
/// here that looks past the current scope, exactly as its name promises.
/// </summary>
internal sealed class FakeLessonSeriesRepository(ITutorContext tutor) : ILessonSeriesRepository
{
    public List<LessonSeries> Items { get; } = [];

    private IEnumerable<LessonSeries> Mine =>
        Items.Where(s => s.TutorTelegramId == (tutor.CurrentTutorTelegramId ?? 0));

    public Task<LessonSeries?> GetByIdAsync(Guid id, bool track = false, CancellationToken ct = default) =>
        Task.FromResult(Mine.SingleOrDefault(s => s.Id == id));

    public Task<IReadOnlyList<LessonSeries>> GetActiveAsync(
        DateOnly? notEndedBefore = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LessonSeries>>(Mine
            .Where(s => notEndedBefore == null || s.EndDate == null || s.EndDate >= notEndedBefore)
            .ToList());

    public Task<IReadOnlyList<LessonSeries>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LessonSeries>>(Mine
            .OrderBy(s => s.CreatedAtUtc)
            .ToList());

    public Task<IReadOnlyList<LessonSeries>> GetStartedNotEndedAcrossAllTutorsAsync(
        DateOnly startedOnOrBefore, DateOnly notEndedBefore, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LessonSeries>>(Items
            .Where(s => s.StartDate <= startedOnOrBefore
                && (s.EndDate == null || s.EndDate >= notEndedBefore))
            .OrderBy(s => s.CreatedAtUtc)
            .ToList());

    public void Add(LessonSeries series) => Items.Add(TenantStamp.Apply(series, tutor));

    public void Update(LessonSeries series) { }
}

/// <summary>
/// The fakes' half of insert stamping: an added row with no owner of its own takes the scope's
/// tenant, which is what <c>AppDbContext.SaveChanges</c> does with the real ones.
/// </summary>
internal static class TenantStamp
{
    public static T Apply<T>(T entity, ITutorContext tutor)
        where T : ITutorOwned =>
        tutor.CurrentTutorTelegramId is { } tenant && entity.TutorTelegramId == 0
            ? entity.OwnedBy(tenant)
            : entity;
}
