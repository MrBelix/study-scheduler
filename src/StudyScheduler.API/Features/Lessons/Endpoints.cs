using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.API.Core.Authentication;
using StudyScheduler.API.Core.ErrorHandling;
using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.API.Core.Time;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Primitives;
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
        return TypedResults.Ok(schedule.Select(LessonResponse.From).ToList());
    }

    /// <summary>Returns a single lesson, scoped to the current tutor.</summary>
    public static async Task<Results<Ok<LessonResponse>, NotFound>> GetById(
        Guid id,
        ClaimsPrincipal principal,
        ILessonRepository repo,
        CancellationToken ct)
    {
        var lesson = await repo.GetByIdAsync(id, principal.GetTelegramId(), ct: ct);
        if (lesson is null)
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
        IUnitOfWork uow,
        TimeProvider clock,
        CancellationToken ct)
    {
        var tutorId = principal.GetTelegramId();

        // The student is resolved first: a missing price falls back to the student's rate, and
        // an absent (or empty-Guid) StudentId must keep answering "Student not found" rather
        // than tripping the factory's programmer-error guard.
        var student = await studentRepo.GetByIdAsync(request.StudentId, tutorId, ct: ct);
        if (student is null)
            return StudentNotFound();

        // The domain factory is the single validator of the lesson's own fields; it must run
        // before the overlap check so invalid input never answers 409.
        var created = Lesson.Create(
            tutorId,
            request.StudentId,
            request.StartUtc,
            request.DurationMinutes,
            request.Price ?? student.Rate,
            clock.GetUtcNow(),
            request.Topic,
            request.Description);
        if (!created.IsSuccess)
            return created.ToValidationProblem();
        var lesson = created.Value;

        var endUtc = request.StartUtc.AddMinutes(request.DurationMinutes);
        var conflicts = await overlapChecker.CheckLessonAsync(tutorId, request.StartUtc, endUtc, ct: ct);
        if (conflicts.Count > 0)
            return Conflict(conflicts);

        repo.Add(lesson);
        await uow.SaveChangesAsync(ct);
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
        LessonPatchService patchService,
        CancellationToken ct)
    {
        var tutorId = principal.GetTelegramId();

        var lesson = await repo.GetByIdAsync(id, tutorId, track: true, ct);
        if (lesson is null)
            return TypedResults.NotFound();

        return ToHttpResult(await patchService.ApplyAsync(lesson, request, tutorId, isNew: false, ct: ct));
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
        LessonPatchService patchService,
        CancellationToken ct)
    {
        var tutorId = principal.GetTelegramId();

        var series = await seriesRepo.GetByIdAsync(seriesId, tutorId, ct: ct);
        if (series is null)
            return TypedResults.NotFound();

        // Already materialized — behave exactly like PATCH /lessons/{id}.
        var existing = await repo.GetBySeriesOccurrenceAsync(seriesId, occurrenceDate, ct);
        if (existing is not null)
            return ToHttpResult(await patchService.ApplyAsync(existing, request, tutorId, isNew: false, ct: ct));

        // The date must be an actual virtual slot of the series (weekday + date window).
        if (!series.IsActive)
            return TypedResults.NotFound();
        var slot = series.GetOccurrences(occurrenceDate, occurrenceDate);
        if (slot.Count == 0)
            return TypedResults.NotFound();

        var lesson = await materializer.MaterializeSlotAsync(series, slot[0], ct);
        return ToHttpResult(await patchService.ApplyAsync(
            lesson, request, tutorId, isNew: true,
            excludeOccurrence: (seriesId, occurrenceDate), ct: ct));
    }

    /// <summary>Maps the patch pipeline's outcome onto the endpoints' HTTP result union.</summary>
    private static Results<Ok<LessonResponse>, NotFound, ValidationProblem, Conflict<LessonConflictResponse>> ToHttpResult(
        LessonPatchOutcome outcome) =>
        outcome switch
        {
            LessonPatchOutcome.Ok ok => TypedResults.Ok(LessonResponse.From(ok.Lesson)),
            LessonPatchOutcome.Validation validation => validation.Failure.ToValidationProblem(),
            LessonPatchOutcome.Conflict conflict => Conflict(conflict.Conflicts),
            LessonPatchOutcome.ConcurrentMaterialization => TypedResults.Conflict(new LessonConflictResponse(
                "The slot was modified concurrently. Retry the request.", [])),
            _ => throw new InvalidOperationException($"Unhandled patch outcome '{outcome.GetType().Name}'."),
        };

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
        var series = await repo.GetByIdAsync(seriesId, principal.GetTelegramId(), ct: ct);
        if (series is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(LessonSeriesResponse.From(series));
    }

    /// <summary>
    /// Creates a weekly series anchored in <c>TimeZoneId</c> (defaults to the tutor's profile
    /// zone; e.g. the student's zone keeps the lesson stable on the student's clock across
    /// mismatched DST transitions). Lessons are not written here — the first range read
    /// materializes them. 409 if the weekly slot collides with existing lessons or another
    /// active series.
    /// </summary>
    public static async Task<Results<Created<LessonSeriesResponse>, ValidationProblem, Conflict<LessonConflictResponse>>> CreateSeries(
        ClaimsPrincipal principal,
        CreateLessonSeriesRequest request,
        ILessonSeriesRepository repo,
        IStudentRepository studentRepo,
        ITutorProfileRepository profileRepo,
        LessonOverlapChecker overlapChecker,
        IUnitOfWork uow,
        TimeProvider clock,
        CancellationToken ct)
    {
        var tutorId = principal.GetTelegramId();

        // Endpoint-level checks that are not single-aggregate invariants: request weekdays get a
        // friendlier example-bearing message, and the time zone id is an HTTP-contract concern
        // (the domain receives an already-resolved TimeZoneInfo).
        var errors = new Dictionary<string, string[]>();
        if (!request.Weekdays.IsValidSet())
            errors["Weekdays"] = ["At least one weekday is required (e.g. \"Monday, Thursday\")."];
        if (request.EndDate is { } endDate && endDate < request.StartDate)
            errors["EndDate"] = ["End date must not precede start date."];
        TimeZoneInfo? explicitZone = null;
        if (!string.IsNullOrWhiteSpace(request.TimeZoneId) && !IanaTimeZone.TryResolve(request.TimeZoneId, out explicitZone))
            errors["TimeZoneId"] = ["A valid IANA time zone id is required (e.g. \"Europe/Kyiv\")."];
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        var profile = await profileRepo.GetAsync(tutorId, ct);
        if (profile is null)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Profile"] = ["Set your time zone first via PUT /profile — series times are defined in it."],
            });

        var student = await studentRepo.GetByIdAsync(request.StudentId, tutorId, ct: ct);
        if (student is null)
            return StudentNotFound();

        // Field invariants (duration, price, ...) are the domain factory's job; it must run
        // before the overlap check so invalid input never answers 409.
        var created = LessonSeries.Create(
            tutorId,
            request.StudentId,
            request.StartDate,
            request.Weekdays,
            request.StartTimeLocal,
            request.DurationMinutes,
            explicitZone ?? profile.TimeZone,
            clock.GetUtcNow(),
            request.Title,
            request.EndDate,
            request.Price);
        if (!created.IsSuccess)
            return created.ToValidationProblem();
        var series = created.Value;

        var conflicts = await overlapChecker.CheckSeriesAsync(series, ct);
        if (conflicts.Count > 0)
            return Conflict(conflicts);

        repo.Add(series);
        await uow.SaveChangesAsync(ct);
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
        IUnitOfWork uow,
        CancellationToken ct)
    {
        var tutorId = principal.GetTelegramId();

        var series = await repo.GetByIdAsync(seriesId, tutorId, track: true, ct);
        if (series is null)
            return TypedResults.NotFound();

        // The domain validates the merged fields (end date vs the series' own start date,
        // price) and reports failures before anything is staged for save.
        var updated = series.UpdateDetails(
            request.Title ?? series.Title,
            request.EndDate ?? series.EndDate,
            request.Price ?? series.Price);
        if (!updated.IsSuccess)
            return updated.ToValidationProblem();

        repo.Update(series);
        await uow.SaveChangesAsync(ct);

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
        IUnitOfWork uow,
        TimeProvider clock,
        CancellationToken ct)
    {
        var tutorId = principal.GetTelegramId();

        var series = await repo.GetByIdAsync(seriesId, tutorId, track: true, ct);
        if (series is null)
            return TypedResults.NotFound();

        series.EndAsOf(clock.GetUtcNow());
        repo.Update(series);
        await uow.SaveChangesAsync(ct);

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
