using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
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
    /// Lists the current tutor's schedule intersecting <c>[from, to)</c>: physical lessons merged
    /// with virtual slots expanded on the fly from active series. Reads never write.
    /// </summary>
    public static async Task<Results<Ok<List<LessonResponse>>, ValidationProblem>> GetMine(
        DateTimeOffset from,
        DateTimeOffset to,
        Guid? studentId,
        ClaimsPrincipal principal,
        LessonMaterializer materializer,
        CancellationToken ct)
    {
        if (ValidateRange(from, to) is { } errors)
            return TypedResults.ValidationProblem(errors);

        var schedule = await materializer.GetScheduleAsync(principal.GetTelegramId(), from, to, studentId, ct);
        return TypedResults.Ok(schedule);
    }

    /// <summary>Returns a single lesson, scoped to the current tutor.</summary>
    public static async Task<Results<Ok<LessonResponse>, NotFound>> GetById(
        Guid id,
        ClaimsPrincipal principal,
        ILessonRepository repo,
        CancellationToken ct)
    {
        var lesson = await repo.GetByIdAsync(id, ct);
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
        TimeProvider clock,
        CancellationToken ct)
    {
        var tutorId = principal.GetTelegramId();

        var errors = new Dictionary<string, string[]>();
        ValidateDuration(request.DurationMinutes, errors);
        ValidatePrice(request.Price, errors);
        ValidateText(request.Topic, Lesson.MaxTopicLength, "Topic", errors);
        ValidateText(request.Description, Lesson.MaxDescriptionLength, "Description", errors);
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        var student = await studentRepo.GetByIdAsync(request.StudentId, ct);
        if (student is null || student.TutorTelegramId != tutorId)
            return StudentNotFound();

        var endUtc = request.StartUtc.AddMinutes(request.DurationMinutes);
        var conflicts = await overlapChecker.CheckLessonAsync(tutorId, request.StartUtc, endUtc, ct: ct);
        if (conflicts.Count > 0)
            return Conflict(conflicts);

        var lesson = Lesson.Create(
            tutorId,
            request.StudentId,
            request.StartUtc,
            request.DurationMinutes,
            request.Price ?? student.Rate,
            clock.GetUtcNow(),
            request.Topic,
            request.Description);

        await repo.AddAsync(lesson, ct);
        return TypedResults.Created($"/lessons/{lesson.Id}", LessonResponse.From(lesson));
    }

    /// <summary>
    /// Partially updates a physical lesson (reschedule, status, price, paid, topic, description),
    /// scoped to the current tutor. Rescheduling (or un-cancelling) re-checks overlaps, excluding
    /// the lesson itself. Virtual slots have no id — use <see cref="UpdateOccurrence"/> for them.
    /// </summary>
    public static async Task<Results<Ok<LessonResponse>, NotFound, ValidationProblem, Conflict<LessonConflictResponse>>> Update(
        Guid id,
        ClaimsPrincipal principal,
        UpdateLessonRequest request,
        ILessonRepository repo,
        LessonOverlapChecker overlapChecker,
        ILogger<LessonMaterializer> logger,
        CancellationToken ct)
    {
        var tutorId = principal.GetTelegramId();

        var lesson = await repo.GetByIdAsync(id, ct);
        if (lesson is null || lesson.TutorTelegramId != tutorId)
            return TypedResults.NotFound();

        return await ApplyPatchAsync(lesson, request, tutorId, repo, overlapChecker, logger, isNew: false, ct: ct);
    }

    /// <summary>
    /// Mutates one slot of a series by its original scheduled date, materializing it on demand:
    /// if no physical lesson exists for the slot yet, one is instantiated from the series (price
    /// snapshot included), the patch is applied and the row is saved — so topics, cancellations
    /// and reschedules of individual occurrences never require pre-materialized rows.
    /// </summary>
    public static async Task<Results<Ok<LessonResponse>, NotFound, ValidationProblem, Conflict<LessonConflictResponse>>> UpdateOccurrence(
        Guid seriesId,
        DateOnly occurrenceDate,
        ClaimsPrincipal principal,
        UpdateLessonRequest request,
        ILessonSeriesRepository seriesRepo,
        ILessonRepository repo,
        LessonMaterializer materializer,
        LessonOverlapChecker overlapChecker,
        ILogger<LessonMaterializer> logger,
        CancellationToken ct)
    {
        var tutorId = principal.GetTelegramId();

        var series = await seriesRepo.GetByIdAsync(seriesId, ct);
        if (series is null || series.TutorTelegramId != tutorId)
            return TypedResults.NotFound();

        // Already materialized — behave exactly like PATCH /lessons/{id}.
        var existing = await repo.GetBySeriesOccurrenceAsync(seriesId, occurrenceDate, ct);
        if (existing is not null)
            return await ApplyPatchAsync(existing, request, tutorId, repo, overlapChecker, logger, isNew: false, ct: ct);

        // The date must be an actual virtual slot of the series (weekday + date window).
        if (!series.IsActive)
            return TypedResults.NotFound();
        var slot = series.GetOccurrences(occurrenceDate, occurrenceDate);
        if (slot.Count == 0)
            return TypedResults.NotFound();

        var lesson = await materializer.MaterializeSlotAsync(series, slot[0], ct);
        return await ApplyPatchAsync(
            lesson, request, tutorId, repo, overlapChecker, logger, isNew: true,
            excludeOccurrence: (seriesId, occurrenceDate), ct: ct);
    }

    /// <summary>
    /// Shared patch pipeline for physical lessons and freshly materialized slots: validate, check
    /// overlaps when the time actually changes (or the lesson is un-cancelled), apply, save.
    /// <paramref name="excludeOccurrence"/> keeps a just-materialized slot from conflicting with
    /// its own series occurrence (the row is not persisted yet, so the checker would otherwise
    /// still see the slot as an unmaterialized occurrence).
    /// </summary>
    private static async Task<Results<Ok<LessonResponse>, NotFound, ValidationProblem, Conflict<LessonConflictResponse>>> ApplyPatchAsync(
        Lesson lesson,
        UpdateLessonRequest request,
        long tutorId,
        ILessonRepository repo,
        LessonOverlapChecker overlapChecker,
        ILogger logger,
        bool isNew,
        (Guid SeriesId, DateOnly OccurrenceDate)? excludeOccurrence = null,
        CancellationToken ct = default)
    {
        var startUtc = request.StartUtc ?? lesson.StartUtc;
        var durationMinutes = request.DurationMinutes ?? lesson.DurationMinutes;
        var status = request.Status ?? lesson.Status;

        var errors = new Dictionary<string, string[]>();
        ValidateDuration(durationMinutes, errors);
        ValidatePrice(request.Price, errors);
        ValidateText(request.Topic, Lesson.MaxTopicLength, "Topic", errors);
        ValidateText(request.Description, Lesson.MaxDescriptionLength, "Description", errors);
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        var timeChanged = startUtc != lesson.StartUtc || durationMinutes != lesson.DurationMinutes;
        var unCancelling = lesson.Status == LessonStatus.Cancelled && status != LessonStatus.Cancelled;
        if (status != LessonStatus.Cancelled && (timeChanged || unCancelling))
        {
            var conflicts = await overlapChecker.CheckLessonAsync(
                tutorId, startUtc, startUtc.AddMinutes(durationMinutes),
                excludeLessonId: lesson.Id, excludeOccurrence: excludeOccurrence, ct: ct);
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
        if (request.Description is not null)
            lesson.UpdateDescription(request.Description);

        if (isNew)
        {
            try
            {
                await repo.AddAsync(lesson, ct);
            }
            catch (DbUpdateException exception)
            {
                // A concurrent request materialized the same slot first — the unique
                // (SeriesId, OccurrenceDate) index rejected the insert. Let the client retry;
                // the retry will hit the physical row and apply as a plain update.
                logger.LogWarning(
                    exception,
                    "Concurrent materialization of occurrence {OccurrenceDate} in series {SeriesId} detected; returning 409",
                    lesson.OccurrenceDate, lesson.SeriesId);

                return TypedResults.Conflict(new LessonConflictResponse(
                    "The slot was modified concurrently. Retry the request.", []));
            }
        }
        else
        {
            await repo.UpdateAsync(lesson, ct);
        }

        return TypedResults.Ok(LessonResponse.From(lesson));
    }

    /// <summary>Lists the current tutor's series (active and cancelled).</summary>
    public static async Task<Ok<List<LessonSeriesResponse>>> GetSeriesList(
        ClaimsPrincipal principal,
        ILessonSeriesRepository repo,
        CancellationToken ct)
    {
        var series = await repo.GetAllByTutorAsync(principal.GetTelegramId(), ct);
        return TypedResults.Ok(series.Select(LessonSeriesResponse.From).ToList());
    }

    /// <summary>Returns a single series, scoped to the current tutor.</summary>
    public static async Task<Results<Ok<LessonSeriesResponse>, NotFound>> GetSeriesById(
        Guid seriesId,
        ClaimsPrincipal principal,
        ILessonSeriesRepository repo,
        CancellationToken ct)
    {
        var series = await repo.GetByIdAsync(seriesId, ct);
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
        TimeProvider clock,
        CancellationToken ct)
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

        var profile = await profileRepo.GetAsync(tutorId, ct);
        if (profile is null)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Profile"] = ["Set your time zone first via PUT /profile — series times are defined in it."],
            });

        var student = await studentRepo.GetByIdAsync(request.StudentId, ct);
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

        var conflicts = await overlapChecker.CheckSeriesAsync(series, ct);
        if (conflicts.Count > 0)
            return Conflict(conflicts);

        await repo.AddAsync(series, ct);
        return TypedResults.Created($"/lessons/series/{series.Id}", LessonSeriesResponse.From(series));
    }

    /// <summary>
    /// Partially updates a series (title, end date, price). Shortening the end date needs no
    /// mass-cancellation: virtual slots beyond the new end simply stop expanding. Physical
    /// (touched) lessons beyond it are left as-is — cancel them individually if needed.
    /// Changing the weekday/time is cancel + recreate.
    /// </summary>
    public static async Task<Results<Ok<LessonSeriesResponse>, NotFound, ValidationProblem>> UpdateSeries(
        Guid seriesId,
        ClaimsPrincipal principal,
        UpdateLessonSeriesRequest request,
        ILessonSeriesRepository repo,
        CancellationToken ct)
    {
        var tutorId = principal.GetTelegramId();

        var series = await repo.GetByIdAsync(seriesId, ct);
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
        await repo.UpdateAsync(series, ct);

        return TypedResults.Ok(LessonSeriesResponse.From(series));
    }

    /// <summary>
    /// Ends a series as of today (in the series' own time zone): future virtual slots stop
    /// expanding, past occurrences keep showing, and no physical rows are touched — the virtual
    /// model needs no mass-cancellation. A series ended before it ever started is deactivated.
    /// </summary>
    public static async Task<Results<Ok<LessonSeriesResponse>, NotFound>> CancelSeries(
        Guid seriesId,
        ClaimsPrincipal principal,
        ILessonSeriesRepository repo,
        TimeProvider clock,
        CancellationToken ct)
    {
        var tutorId = principal.GetTelegramId();

        var series = await repo.GetByIdAsync(seriesId, ct);
        if (series is null || series.TutorTelegramId != tutorId)
            return TypedResults.NotFound();

        series.EndAsOf(clock.GetUtcNow());
        await repo.UpdateAsync(series, ct);

        return TypedResults.Ok(LessonSeriesResponse.From(series));
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

    private static void ValidateText(string? value, int maxLength, string field, Dictionary<string, string[]> errors)
    {
        if (value?.Trim().Length > maxLength)
            errors[field] = [$"{field} must not exceed {maxLength} characters."];
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
