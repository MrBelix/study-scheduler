using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Core.Scheduling;

/// <summary>
/// Internal projection of one slot of a tutor's merged schedule — either a physical
/// <see cref="Lesson"/> row or a virtual series occurrence expanded in memory
/// (<see cref="IsVirtual"/> true, <see cref="Id"/> null). This is the cross-slice shape:
/// the Lessons slice maps it to its HTTP contract, the Notifications slice plans and
/// formats messages from it — neither depends on the other's types.
/// </summary>
public sealed record ScheduleSlot(
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
    public static ScheduleSlot From(Lesson lesson) => new(
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

    /// <summary>An unmaterialized series slot, generated in memory for the requested range.</summary>
    public static ScheduleSlot Virtual(LessonSeries series, LessonOccurrence occurrence, decimal price) => new(
        Id: null,
        series.StudentId,
        series.Id,
        occurrence.OccurrenceDate,
        occurrence.StartUtc,
        occurrence.EndUtc,
        series.DurationMinutes,
        LessonStatus.Scheduled,
        price,
        IsPaid: false,
        Topic: null,
        Description: null,
        IsVirtual: true,
        series.CreatedAtUtc);
}
