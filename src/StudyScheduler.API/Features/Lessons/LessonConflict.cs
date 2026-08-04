namespace StudyScheduler.API.Features.Lessons;

/// <summary>
/// One conflicting slot found by <see cref="LessonOverlapChecker"/>: either an existing lesson
/// (<see cref="LessonId"/> — the same id <c>GET /lessons/{id}</c> takes) or a future occurrence of an
/// active series (<see cref="SeriesId"/>/<see cref="SeriesTitle"/>), which has no lesson of its own
/// yet.
/// </summary>
public sealed record LessonConflict(
    Guid? LessonId,
    Guid? SeriesId,
    string? SeriesTitle,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc);
