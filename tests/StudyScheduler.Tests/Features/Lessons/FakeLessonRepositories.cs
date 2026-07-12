using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.Tests.Features.Lessons;

/// <summary>In-memory <see cref="ILessonRepository"/> mirroring the EF query semantics.</summary>
internal sealed class FakeLessonRepository : ILessonRepository
{
    public List<Lesson> Items { get; } = [];

    public Task<Lesson?> GetByIdAsync(Guid id, long tutorTelegramId, bool track = false, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(l => l.Id == id && l.TutorTelegramId == tutorTelegramId));

    public Task<IReadOnlyList<Lesson>> GetByTutorInRangeAsync(
        long tutorTelegramId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        Guid? studentId = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Lesson>>(Items
            .Where(l => l.TutorTelegramId == tutorTelegramId
                && l.StartUtc < toUtc
                && l.EndUtc > fromUtc
                && (studentId == null || l.StudentId == studentId))
            .OrderBy(l => l.StartUtc)
            .ToList());

    public Task<IReadOnlyList<Lesson>> GetOverlappingAsync(
        long tutorTelegramId, DateTimeOffset startUtc, DateTimeOffset endUtc,
        Guid? excludeLessonId = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Lesson>>(Items
            .Where(l => l.TutorTelegramId == tutorTelegramId
                && l.Status != LessonStatus.Cancelled
                && l.StartUtc < endUtc
                && l.EndUtc > startUtc
                && (excludeLessonId == null || l.Id != excludeLessonId))
            .ToList());

    public Task<IReadOnlyList<Lesson>> GetFromDateAsync(
        long tutorTelegramId, DateTimeOffset fromUtc, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Lesson>>(Items
            .Where(l => l.TutorTelegramId == tutorTelegramId
                && l.Status != LessonStatus.Cancelled
                && l.StartUtc >= fromUtc)
            .ToList());

    public Task<IReadOnlyList<SeriesSlot>> GetMaterializedSlotsAsync(
        IReadOnlyCollection<Guid> seriesIds, DateOnly fromLocal, DateOnly toLocal, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SeriesSlot>>(Items
            .Where(l => l.SeriesId is { } sid && seriesIds.Contains(sid)
                && l.OccurrenceDate is { } d && d >= fromLocal && d <= toLocal)
            .Select(l => new SeriesSlot(l.SeriesId!.Value, l.OccurrenceDate!.Value))
            .ToList());

    public Task<Lesson?> GetBySeriesOccurrenceAsync(
        Guid seriesId, DateOnly occurrenceDate, long tutorTelegramId, bool track = false, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(l =>
            l.SeriesId == seriesId && l.OccurrenceDate == occurrenceDate && l.TutorTelegramId == tutorTelegramId));

    public Task<IReadOnlyList<Lesson>> GetMaterializedForSeriesFromAsync(
        Guid seriesId, long tutorTelegramId, DateOnly fromOccurrenceDate, bool track = false, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Lesson>>(Items
            .Where(l => l.SeriesId == seriesId && l.TutorTelegramId == tutorTelegramId
                && l.OccurrenceDate is { } d && d >= fromOccurrenceDate)
            .ToList());

    public void Add(Lesson lesson) => Items.Add(lesson);

    public void Update(Lesson lesson) { }

    public void Remove(Lesson lesson) => Items.Remove(lesson);
}

/// <summary>In-memory <see cref="ILessonSeriesRepository"/> mirroring the EF query semantics.</summary>
internal sealed class FakeLessonSeriesRepository : ILessonSeriesRepository
{
    public List<LessonSeries> Items { get; } = [];

    public Task<LessonSeries?> GetByIdAsync(Guid id, long tutorTelegramId, bool track = false, CancellationToken ct = default) =>
        Task.FromResult(Items.SingleOrDefault(s => s.Id == id && s.TutorTelegramId == tutorTelegramId));

    public Task<IReadOnlyList<LessonSeries>> GetActiveByTutorAsync(
        long tutorTelegramId, DateOnly? notEndedBefore = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LessonSeries>>(Items
            .Where(s => s.TutorTelegramId == tutorTelegramId
                && (notEndedBefore == null || s.EndDate == null || s.EndDate >= notEndedBefore))
            .ToList());

    public Task<IReadOnlyList<LessonSeries>> GetAllByTutorAsync(long tutorTelegramId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LessonSeries>>(Items
            .Where(s => s.TutorTelegramId == tutorTelegramId)
            .OrderBy(s => s.CreatedAtUtc)
            .ToList());

    public void Add(LessonSeries series) => Items.Add(series);

    public void Update(LessonSeries series) { }
}
