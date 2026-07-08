using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>Request body for creating a one-off lesson. <c>Price</c> null falls back to the student's rate.</summary>
public sealed record CreateLessonRequest(
    Guid StudentId,
    DateTimeOffset StartUtc,
    int DurationMinutes,
    decimal? Price,
    string? Topic,
    string? Description);

/// <summary>
/// Request body for creating a recurring series. <c>Weekdays</c> is a flags combination
/// (e.g. <c>"Monday, Thursday"</c>); <c>StartTimeLocal</c> is the wall clock in the series'
/// anchor zone: <c>TimeZoneId</c> when set (e.g. the student's zone), otherwise the tutor's
/// profile zone. <c>EndDate</c> null makes the series open-ended. <c>Price</c> null falls back
/// to the student's rate at materialization.
/// </summary>
public sealed record CreateLessonSeriesRequest(
    Guid StudentId,
    string? Title,
    DateOnly StartDate,
    Weekdays Weekdays,
    TimeOnly StartTimeLocal,
    int DurationMinutes,
    DateOnly? EndDate,
    decimal? Price,
    string? TimeZoneId = null);

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

/// <summary>Partial update of a series. Changing the weekday/time means cancel + recreate.</summary>
public sealed record UpdateLessonSeriesRequest(
    string? Title,
    DateOnly? EndDate,
    decimal? Price);

/// <summary>
/// Lesson projection returned to the client. <c>IsVirtual</c> true means the slot was expanded
/// on the fly from its series and has no database row (and thus no <c>Id</c>) — mutate it via
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

    /// <summary>Maps the internal schedule projection 1:1 — the wire shape must not change.</summary>
    public static LessonResponse From(ScheduleSlot slot) => new(
        slot.Id,
        slot.StudentId,
        slot.SeriesId,
        slot.OccurrenceDate,
        slot.StartUtc,
        slot.EndUtc,
        slot.DurationMinutes,
        slot.Status,
        slot.Price,
        slot.IsPaid,
        slot.Topic,
        slot.Description,
        slot.IsVirtual,
        slot.CreatedAtUtc);
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
