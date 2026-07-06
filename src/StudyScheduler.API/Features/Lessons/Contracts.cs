using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>Request body for creating a one-off lesson. <c>Price</c> null falls back to the student's rate.</summary>
public sealed record CreateLessonRequest(
    Guid StudentId,
    DateTimeOffset StartUtc,
    int DurationMinutes,
    decimal? Price,
    string? Topic);

/// <summary>
/// Request body for creating a recurring series. <c>Weekdays</c> is a flags combination
/// (e.g. <c>"Monday, Thursday"</c>); times are the tutor's local wall clock (the time zone comes
/// from the tutor profile); <c>EndDate</c> null makes the series open-ended. <c>Price</c> null
/// falls back to the student's rate at materialization.
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
/// </summary>
public sealed record UpdateLessonRequest(
    DateTimeOffset? StartUtc,
    int? DurationMinutes,
    LessonStatus? Status,
    decimal? Price,
    bool? IsPaid,
    string? Topic);

/// <summary>Partial update of a series. Changing the weekday/time means cancel + recreate.</summary>
public sealed record UpdateLessonSeriesRequest(
    string? Title,
    DateOnly? EndDate,
    decimal? Price);

/// <summary>Lesson projection returned to the client.</summary>
public sealed record LessonResponse(
    Guid Id,
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
        lesson.CreatedAtUtc);
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
    bool IsActive,
    DateTimeOffset CreatedAtUtc)
{
    public static LessonSeriesResponse From(LessonSeries series) => new(
        series.Id,
        series.StudentId,
        series.Title,
        series.StartDate,
        series.EndDate,
        series.Weekdays,
        series.StartTimeLocal,
        series.DurationMinutes,
        series.TimeZone.Id,
        series.Price,
        series.IsActive,
        series.CreatedAtUtc);
}

/// <summary>
/// One conflicting slot: either an existing lesson (<see cref="LessonId"/>) or a future
/// occurrence of an active series (<see cref="SeriesId"/>/<see cref="SeriesTitle"/>).
/// </summary>
public sealed record LessonConflict(
    Guid? LessonId,
    Guid? SeriesId,
    string? SeriesTitle,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc);

/// <summary>409 payload listing everything the requested time collides with.</summary>
public sealed record LessonConflictResponse(string Message, List<LessonConflict> Conflicts);

public sealed record CancelSeriesResponse(int CancelledCount);
