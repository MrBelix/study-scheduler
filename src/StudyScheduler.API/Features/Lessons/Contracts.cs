using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>
/// Request body of the one create route: a one-off lesson when <c>Repeat</c> is absent, a weekly
/// series when it is present — the client never picks between two routes, it fills in one form.
/// <c>Date</c> is the lesson day (the series' start day) and <c>StartTimeLocal</c> the wall clock in
/// the tutor's PROFILE time zone, so both branches place a lesson by the same rule and the profile
/// must be set either way. <c>Price</c> null falls back to the student's rate (for a series, at
/// generation time).
///
/// A one-off lesson used to be created from an absolute <c>StartUtc</c> chosen by the client; it is
/// now resolved from local wall clock + profile zone through the same <see cref="WallClock"/> seam
/// the series path has always used. Deliberate: one time vocabulary for one form.
/// </summary>
public sealed record CreateLessonRequest(
    Guid StudentId,
    DateOnly Date,
    TimeOnly StartTimeLocal,
    int DurationMinutes,
    decimal? Price,
    string? Topic,
    LessonRepeatRequest? Repeat);

/// <summary>
/// The "Repeat: weekly" half of <see cref="CreateLessonRequest"/>. <c>Weekdays</c> is a flags
/// combination (e.g. <c>"Monday, Thursday"</c>) — the same shape
/// <see cref="LessonSeriesResponse.Weekdays"/> is serialized in; <c>EndDate</c> null makes the
/// series open-ended. <c>Title</c> names the series: a series has no per-lesson topic, so the
/// request's <c>Topic</c> applies to the one-off branch only.
/// </summary>
public sealed record LessonRepeatRequest(
    Weekdays Weekdays,
    string? Title,
    DateOnly? EndDate);

/// <summary>
/// Partial update — only non-null fields are applied. Cancelling is <c>Status = "Cancelled"</c>.
/// Applies to any lesson id: a one-off lesson and a single occurrence of a series alike.
/// </summary>
public sealed record UpdateLessonRequest(
    DateTimeOffset? StartUtc,
    int? DurationMinutes,
    LessonStatus? Status,
    decimal? Price,
    bool? IsPaid,
    string? Topic,
    string? Description);

/// <summary>
/// Body of the bulk settle route: the lessons the tutor has just been paid for, by id. Omitting the
/// field reads as an empty selection, which is refused exactly as an empty array is — settling
/// nothing is never what the client meant.
/// </summary>
public sealed record SettleLessonsRequest(IReadOnlyList<Guid>? LessonIds);

/// <summary>
/// What the bulk settle answers with: how many of the sent ids are now paid. Ids that were already
/// settled count too — the call is idempotent, so the number is "what you asked for is done", not
/// "what changed".
/// </summary>
public sealed record SettleLessonsResponse(int Settled);

/// <summary>
/// Partial update of a series — a full edit, schedule included. A null field means "not provided"
/// (this API's convention everywhere), so making an ended series open-ended again cannot be
/// expressed by <c>EndDate</c> alone and is asked for with <c>ClearEndDate</c> instead; sending both
/// is refused as contradictory.
///
/// <c>Weekdays</c>, <c>StartTimeLocal</c> and <c>DurationMinutes</c> edit the weekly schedule in
/// place (the time zone is not editable — it is the one the series was created in). Any of them, and
/// any change to the window, rewrites the lessons the series generated for the future;
/// <see cref="KeepCustomized"/> decides what happens to the occurrences somebody edited by hand:
/// <c>true</c> (the default) leaves them exactly where they are, <c>false</c> replaces them along
/// with the rest. Lessons that already started, and completed ones whenever they sit, are never
/// touched either way.
/// </summary>
public sealed record UpdateLessonSeriesRequest(
    string? Title,
    decimal? Price,
    DateOnly? EndDate,
    bool ClearEndDate = false,
    Weekdays? Weekdays = null,
    TimeOnly? StartTimeLocal = null,
    int? DurationMinutes = null,
    bool KeepCustomized = true);

/// <summary>
/// Body of the cancel route — optional, so a bodiless <c>POST</c> still means "cancel, keeping my
/// hand-edited lessons". <see cref="KeepCustomized"/> reads exactly as it does on
/// <see cref="UpdateLessonSeriesRequest"/>: <c>false</c> also sweeps away the future occurrences
/// somebody edited individually.
/// </summary>
public sealed record CancelLessonSeriesRequest(bool KeepCustomized = true);

/// <summary>
/// Lesson projection returned to the client. <c>Id</c> is the id of the lesson row — the only way to
/// address it (<c>GET</c>/<c>PATCH /lessons/{id}</c>), for series occurrences as much as for one-off
/// lessons. <c>SeriesId</c> and <c>OccurrenceDate</c> describe where the lesson sits in its series:
/// the date it was originally scheduled on, which stays put when the single lesson is rescheduled to
/// another time.
/// </summary>
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
    string? Description,
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

/// <summary>
/// What the one create route returns: exactly one of the two is non-null — <c>Lesson</c> when the
/// request had no <c>Repeat</c>, <c>Series</c> when it had one. The client switches on which of
/// them arrived instead of on the route it called, and each is the very payload
/// <c>GET /lessons/{id}</c> and <c>GET /lessons/series/{id}</c> serve.
/// </summary>
public sealed record CreateLessonResponse(LessonResponse? Lesson, LessonSeriesResponse? Series);

/// <summary>409 payload listing everything the requested time collides with.</summary>
public sealed record LessonConflictResponse(string Message, IReadOnlyList<LessonConflict> Conflicts);

/// <summary>
/// Result of ending a series: the updated series plus the future lessons that were swept away with
/// the schedule that ceased to exist (so the client can tell the tutor which lessons went away).
/// </summary>
public sealed record CancelSeriesResponse(LessonSeriesResponse Series, IReadOnlyList<LessonResponse> RemovedLessons);

/// <summary>
/// Result of updating a series: the updated series plus the future lessons the new schedule no
/// longer covers. A lesson the edit deleted and immediately re-created on the same date is NOT
/// reported — the calendar still holds that lesson; only genuine losses are listed.
/// </summary>
public sealed record UpdateSeriesResponse(LessonSeriesResponse Series, IReadOnlyList<LessonResponse> RemovedLessons);
