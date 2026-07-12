using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>
/// Request body for a one-off lesson. <c>StartUtc</c> is the absolute instant; <c>Price</c> null
/// falls back to the student's rate.
/// </summary>
public sealed record CreateLessonRequest(
    Guid StudentId,
    DateTimeOffset StartUtc,
    int DurationMinutes,
    decimal? Price,
    string? Topic,
    string? Description);

/// <summary>
/// Request body for a recurring series. <c>Weekdays</c> is a flags combination
/// (e.g. <c>"Monday, Thursday"</c>); <c>StartTimeLocal</c> is the wall clock in the tutor's profile
/// time zone (the series' anchor). <c>EndDate</c> null makes the series open-ended.
/// </summary>
public sealed record CreateLessonSeriesRequest(
    Guid StudentId,
    string? Title,
    DateOnly StartDate,
    Weekdays Weekdays,
    TimeOnly StartTimeLocal,
    int DurationMinutes,
    DateOnly? EndDate,
    decimal? Price);

/// <summary>
/// Partial update — only non-null fields are applied. Cancelling is <c>Status = "Cancelled"</c>.
/// Also the body of the virtual-slot mutation endpoint, which materializes the slot first.
/// </summary>
public sealed record UpdateLessonRequest(
    DateTimeOffset? StartUtc,
    int? DurationMinutes,
    LessonStatus? Status,
    decimal? Price,
    bool? IsPaid,
    string? Topic,
    string? Description);

/// <summary>Partial update of a series' metadata. Changing the schedule is a separate operation.</summary>
public sealed record UpdateLessonSeriesRequest(string? Title, decimal? Price);

/// <summary>
/// Lesson projection returned to the client. <c>IsVirtual</c> true means the slot was expanded on
/// the fly from its series and has no database row (and thus no <c>Id</c>) — mutate it via
/// <c>PATCH /lessons/series/{seriesId}/occurrences/{occurrenceDate}</c>, which materializes it.
/// </summary>
public sealed record LessonResponse(
    Guid? Id,
    Guid StudentId,
    Guid? SeriesId,
    DateOnly? OccurrenceDate,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int DurationMinutes,
    LessonStatus Status,
    decimal Price,
    bool IsPaid,
    string? Topic,
    string? Description,
    bool IsVirtual,
    DateTimeOffset CreatedAtUtc)
{
    public static LessonResponse From(Lesson lesson) => new(
        lesson.Id,
        lesson.StudentId,
        lesson.SeriesId,
        lesson.OccurrenceDate,
        lesson.StartUtc,
        lesson.EndUtc,
        lesson.DurationMinutes,
        lesson.Status,
        lesson.Price,
        lesson.IsPaid,
        lesson.Topic,
        lesson.Description,
        IsVirtual: false,
        lesson.CreatedAtUtc);

    public static LessonResponse From(ScheduleEntry entry) => new(
        entry.Id,
        entry.StudentId,
        entry.SeriesId,
        entry.OccurrenceDate,
        entry.StartUtc,
        entry.EndUtc,
        entry.DurationMinutes,
        entry.Status,
        entry.Price,
        entry.IsPaid,
        entry.Topic,
        entry.Description,
        entry.IsVirtual,
        entry.CreatedAtUtc);
}

/// <summary>Series projection returned to the client.</summary>
public sealed record LessonSeriesResponse(
    Guid Id,
    Guid StudentId,
    string? Title,
    DateOnly StartDate,
    DateOnly? EndDate,
    Weekdays Weekdays,
    TimeOnly StartTimeLocal,
    int DurationMinutes,
    string TimeZoneId,
    decimal? Price,
    DateTimeOffset CreatedAtUtc)
{
    public static LessonSeriesResponse From(LessonSeries series) => new(
        series.Id,
        series.StudentId,
        series.Title,
        series.StartDate,
        series.EndDate,
        series.Pattern.Days,
        series.Pattern.StartTimeLocal,
        series.Pattern.DurationMinutes,
        series.Pattern.TimeZone.Id,
        series.Price,
        series.CreatedAtUtc);
}

/// <summary>409 payload listing everything the requested time collides with.</summary>
public sealed record LessonConflictResponse(string Message, IReadOnlyList<LessonConflict> Conflicts);

/// <summary>
/// Result of ending a series: the updated series plus the future materialized overrides that were
/// removed (so the client can tell the tutor which individually-edited lessons went away).
/// </summary>
public sealed record CancelSeriesResponse(LessonSeriesResponse Series, IReadOnlyList<LessonResponse> RemovedLessons);
