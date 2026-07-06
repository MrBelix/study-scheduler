using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.API.Core.Authentication;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Students;
using StudyScheduler.Domain.Tutors;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>HTTP handlers for the Lessons feature. Wired to routes in <see cref="LessonsModule"/>.</summary>
internal static class Endpoints
{
    private const int MaxRangeDays = 366;

    /// <summary>
    /// Lists the current tutor's lessons intersecting <c>[from, to)</c>, materializing occurrences
    /// of active series in that range first (idempotent side effect).
    /// </summary>
    public static async Task<Results<Ok<List<LessonResponse>>, ValidationProblem>> GetMine(
        DateTimeOffset from,
        DateTimeOffset to,
        Guid? studentId,
        ClaimsPrincipal principal,
        ILessonRepository repo,
        LessonMaterializer materializer)
    {
        if (ValidateRange(from, to) is { } errors)
            return TypedResults.ValidationProblem(errors);

        var tutorId = principal.GetTelegramId();
        await materializer.MaterializeAsync(tutorId, from, to);

        var lessons = await repo.GetByTutorInRangeAsync(tutorId, from, to, studentId);
        return TypedResults.Ok(lessons.Select(LessonResponse.From).ToList());
    }

    /// <summary>Returns a single lesson, scoped to the current tutor.</summary>
    public static async Task<Results<Ok<LessonResponse>, NotFound>> GetById(
        Guid id,
        ClaimsPrincipal principal,
        ILessonRepository repo)
    {
        var lesson = await repo.GetByIdAsync(id);
        if (lesson is null || lesson.TutorTelegramId != principal.GetTelegramId())
            return TypedResults.NotFound();

        return TypedResults.Ok(LessonResponse.From(lesson));
    }

    /// <summary>Creates a one-off lesson; 409 if the slot collides with a lesson or a series.</summary>
    public static async Task<Results<Created<LessonResponse>, ValidationProblem, Conflict<LessonConflictResponse>>> Create(
        ClaimsPrincipal principal,
        CreateLessonRequest request,
        ILessonRepository repo,
        IStudentRepository studentRepo,
        LessonOverlapChecker overlapChecker,
        TimeProvider clock)
    {
        var tutorId = principal.GetTelegramId();

        var errors = new Dictionary<string, string[]>();
        ValidateDuration(request.DurationMinutes, errors);
        ValidatePrice(request.Price, errors);
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        var student = await studentRepo.GetByIdAsync(request.StudentId);
        if (student is null || student.TutorTelegramId != tutorId)
            return StudentNotFound();

        var endUtc = request.StartUtc.AddMinutes(request.DurationMinutes);
        var conflicts = await overlapChecker.CheckLessonAsync(tutorId, request.StartUtc, endUtc);
        if (conflicts.Count > 0)
            return Conflict(conflicts);

        var lesson = Lesson.Create(
            tutorId,
            request.StudentId,
            request.StartUtc,
            request.DurationMinutes,
            request.Price ?? student.Rate,
            clock.GetUtcNow(),
            request.Topic);

        await repo.AddAsync(lesson);
        return TypedResults.Created($"/lessons/{lesson.Id}", LessonResponse.From(lesson));
    }

    /// <summary>
    /// Partially updates a lesson (reschedule, status, price, paid, topic), scoped to the current
    /// tutor. Rescheduling (or un-cancelling) re-checks overlaps, excluding the lesson itself.
    /// </summary>
    public static async Task<Results<Ok<LessonResponse>, NotFound, ValidationProblem, Conflict<LessonConflictResponse>>> Update(
        Guid id,
        ClaimsPrincipal principal,
        UpdateLessonRequest request,
        ILessonRepository repo,
        LessonOverlapChecker overlapChecker)
    {
        var tutorId = principal.GetTelegramId();

        var lesson = await repo.GetByIdAsync(id);
        if (lesson is null || lesson.TutorTelegramId != tutorId)
            return TypedResults.NotFound();

        var startUtc = request.StartUtc ?? lesson.StartUtc;
        var durationMinutes = request.DurationMinutes ?? lesson.DurationMinutes;
        var status = request.Status ?? lesson.Status;

        var errors = new Dictionary<string, string[]>();
        ValidateDuration(durationMinutes, errors);
        ValidatePrice(request.Price, errors);
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        var timeChanged = startUtc != lesson.StartUtc || durationMinutes != lesson.DurationMinutes;
        var unCancelling = lesson.Status == LessonStatus.Cancelled && status != LessonStatus.Cancelled;
        if (status != LessonStatus.Cancelled && (timeChanged || unCancelling))
        {
            var conflicts = await overlapChecker.CheckLessonAsync(
                tutorId, startUtc, startUtc.AddMinutes(durationMinutes), excludeLessonId: lesson.Id);
            if (conflicts.Count > 0)
                return Conflict(conflicts);
        }

        if (timeChanged)
            lesson.Reschedule(startUtc, durationMinutes);
        if (request.Status is { } newStatus)
            lesson.ChangeStatus(newStatus);
        if (request.Price is { } price)
            lesson.SetPrice(price);
        if (request.IsPaid is { } isPaid)
            lesson.SetPaid(isPaid);
        if (request.Topic is not null)
            lesson.UpdateTopic(request.Topic);

        await repo.UpdateAsync(lesson);
        return TypedResults.Ok(LessonResponse.From(lesson));
    }

    /// <summary>Lists the current tutor's series (active and cancelled).</summary>
    public static async Task<Ok<List<LessonSeriesResponse>>> GetSeriesList(
        ClaimsPrincipal principal,
        ILessonSeriesRepository repo)
    {
        var series = await repo.GetAllByTutorAsync(principal.GetTelegramId());
        return TypedResults.Ok(series.Select(LessonSeriesResponse.From).ToList());
    }

    /// <summary>Returns a single series, scoped to the current tutor.</summary>
    public static async Task<Results<Ok<LessonSeriesResponse>, NotFound>> GetSeriesById(
        Guid seriesId,
        ClaimsPrincipal principal,
        ILessonSeriesRepository repo)
    {
        var series = await repo.GetByIdAsync(seriesId);
        if (series is null || series.TutorTelegramId != principal.GetTelegramId())
            return TypedResults.NotFound();

        return TypedResults.Ok(LessonSeriesResponse.From(series));
    }

    /// <summary>
    /// Creates a weekly series in the tutor's profile time zone. Lessons are not written here —
    /// the first range read materializes them. 409 if the weekly slot collides with existing
    /// lessons or another active series.
    /// </summary>
    public static async Task<Results<Created<LessonSeriesResponse>, ValidationProblem, Conflict<LessonConflictResponse>>> CreateSeries(
        ClaimsPrincipal principal,
        CreateLessonSeriesRequest request,
        ILessonSeriesRepository repo,
        IStudentRepository studentRepo,
        ITutorProfileRepository profileRepo,
        LessonOverlapChecker overlapChecker,
        TimeProvider clock)
    {
        var tutorId = principal.GetTelegramId();

        var errors = new Dictionary<string, string[]>();
        ValidateDuration(request.DurationMinutes, errors);
        ValidatePrice(request.Price, errors);
        if (!request.Weekdays.IsValidSet())
            errors["Weekdays"] = ["At least one weekday is required (e.g. \"Monday, Thursday\")."];
        if (request.EndDate is { } endDate && endDate < request.StartDate)
            errors["EndDate"] = ["End date must not precede start date."];
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        var profile = await profileRepo.GetAsync(tutorId);
        if (profile is null)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Profile"] = ["Set your time zone first via PUT /profile — series times are defined in it."],
            });

        var student = await studentRepo.GetByIdAsync(request.StudentId);
        if (student is null || student.TutorTelegramId != tutorId)
            return StudentNotFound();

        var series = LessonSeries.Create(
            tutorId,
            request.StudentId,
            request.StartDate,
            request.Weekdays,
            request.StartTimeLocal,
            request.DurationMinutes,
            profile.TimeZone,
            clock.GetUtcNow(),
            request.Title,
            request.EndDate,
            request.Price);

        var conflicts = await overlapChecker.CheckSeriesAsync(series);
        if (conflicts.Count > 0)
            return Conflict(conflicts);

        await repo.AddAsync(series);
        return TypedResults.Created($"/lessons/series/{series.Id}", LessonSeriesResponse.From(series));
    }

    /// <summary>
    /// Partially updates a series (title, end date, price). Shortening the end date cancels the
    /// already-materialized scheduled lessons that fall beyond it. Changing the weekday/time is
    /// cancel + recreate.
    /// </summary>
    public static async Task<Results<Ok<LessonSeriesResponse>, NotFound, ValidationProblem>> UpdateSeries(
        Guid seriesId,
        ClaimsPrincipal principal,
        UpdateLessonSeriesRequest request,
        ILessonSeriesRepository repo,
        ILessonRepository lessonRepo)
    {
        var tutorId = principal.GetTelegramId();

        var series = await repo.GetByIdAsync(seriesId);
        if (series is null || series.TutorTelegramId != tutorId)
            return TypedResults.NotFound();

        var endDate = request.EndDate ?? series.EndDate;
        var errors = new Dictionary<string, string[]>();
        ValidatePrice(request.Price, errors);
        if (endDate is { } end && end < series.StartDate)
            errors["EndDate"] = ["End date must not precede start date."];
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        series.UpdateDetails(
            request.Title ?? series.Title,
            endDate,
            request.Price ?? series.Price);
        await repo.UpdateAsync(series);

        if (endDate is { } newEnd)
        {
            var beyondEnd = (await lessonRepo.GetBySeriesIdAsync(tutorId, series.Id))
                .Where(l => l.Status == LessonStatus.Scheduled && l.OccurrenceDate > newEnd)
                .ToList();
            if (beyondEnd.Count > 0)
            {
                beyondEnd.ForEach(l => l.ChangeStatus(LessonStatus.Cancelled));
                await lessonRepo.UpdateRangeAsync(beyondEnd);
            }
        }

        return TypedResults.Ok(LessonSeriesResponse.From(series));
    }

    /// <summary>
    /// Cancels a series: deactivates it (no further materialization) and cancels its future
    /// scheduled lessons. Past and completed lessons stay untouched.
    /// </summary>
    public static async Task<Results<Ok<CancelSeriesResponse>, NotFound>> CancelSeries(
        Guid seriesId,
        ClaimsPrincipal principal,
        ILessonSeriesRepository repo,
        ILessonRepository lessonRepo,
        TimeProvider clock)
    {
        var tutorId = principal.GetTelegramId();

        var series = await repo.GetByIdAsync(seriesId);
        if (series is null || series.TutorTelegramId != tutorId)
            return TypedResults.NotFound();

        series.Deactivate();
        await repo.UpdateAsync(series);

        var now = clock.GetUtcNow();
        var toCancel = (await lessonRepo.GetBySeriesIdAsync(tutorId, series.Id))
            .Where(l => l.Status == LessonStatus.Scheduled && l.StartUtc >= now)
            .ToList();
        toCancel.ForEach(l => l.ChangeStatus(LessonStatus.Cancelled));
        if (toCancel.Count > 0)
            await lessonRepo.UpdateRangeAsync(toCancel);

        return TypedResults.Ok(new CancelSeriesResponse(toCancel.Count));
    }

    private static Dictionary<string, string[]>? ValidateRange(DateTimeOffset from, DateTimeOffset to)
    {
        var errors = new Dictionary<string, string[]>();
        if (to <= from)
            errors["To"] = ["'to' must be after 'from'."];
        else if ((to - from).TotalDays > MaxRangeDays)
            errors["To"] = [$"Range must not exceed {MaxRangeDays} days."];

        return errors.Count == 0 ? null : errors;
    }

    private static void ValidateDuration(int durationMinutes, Dictionary<string, string[]> errors)
    {
        if (durationMinutes is < Lesson.MinDurationMinutes or > Lesson.MaxDurationMinutes)
            errors["DurationMinutes"] =
                [$"Duration must be between {Lesson.MinDurationMinutes} and {Lesson.MaxDurationMinutes} minutes."];
    }

    private static void ValidatePrice(decimal? price, Dictionary<string, string[]> errors)
    {
        if (price is < 0)
            errors["Price"] = ["Price must be zero or positive."];
    }

    // Same message whether the student does not exist or belongs to another tutor — existence
    // must not leak across tenants (same principle as the 404 on /students/{id}).
    private static ValidationProblem StudentNotFound() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            ["StudentId"] = ["Student not found."],
        });

    private static Conflict<LessonConflictResponse> Conflict(List<LessonConflict> conflicts) =>
        TypedResults.Conflict(new LessonConflictResponse(
            "The requested time overlaps existing lessons or series.", conflicts));
}
