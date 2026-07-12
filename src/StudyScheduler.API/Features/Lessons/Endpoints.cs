using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.API.Core.Authentication;
using StudyScheduler.API.Core.ErrorHandling;
using StudyScheduler.API.Core.Scheduling;
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
        ScheduleReader reader,
        CancellationToken ct)
    {
        if (ValidateRange(from, to) is { } errors)
            return TypedResults.ValidationProblem(errors);

        var schedule = await reader.GetScheduleAsync(principal.GetTelegramId(), from, to, studentId, ct);
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
        return lesson is null ? TypedResults.NotFound() : TypedResults.Ok(LessonResponse.From(lesson));
    }

    /// <summary>Creates a one-off lesson at an absolute <c>StartUtc</c>. 409 on collision.</summary>
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

        var student = await studentRepo.GetByIdAsync(request.StudentId, tutorId, ct: ct);
        if (student is null)
            return StudentNotFound();

        var created = Lesson.Create(
            tutorId, request.StudentId, request.StartUtc, request.DurationMinutes,
            request.Price ?? student.Rate, clock.GetUtcNow(), request.Topic, request.Description);
        if (!created.IsSuccess)
            return created.ToValidationProblem();
        var lesson = created.Value;

        var conflicts = await overlapChecker.CheckLessonAsync(
            tutorId, request.StartUtc, request.StartUtc.AddMinutes(request.DurationMinutes), ct: ct);
        if (conflicts.Count > 0)
            return Conflict(conflicts);

        repo.Add(lesson);
        await uow.SaveChangesAsync(ct);
        return TypedResults.Created($"/lessons/{lesson.Id}", LessonResponse.From(lesson));
    }

    /// <summary>Partially updates a physical lesson, scoped to the current tutor.</summary>
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

    /// <summary>Lists the current tutor's series (active and ended).</summary>
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
        return series is null ? TypedResults.NotFound() : TypedResults.Ok(LessonSeriesResponse.From(series));
    }

    /// <summary>
    /// Creates a weekly series anchored in the tutor's profile time zone. Lessons are not written
    /// here — the first read expands them. 409 if the weekly slot collides.
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

        // Friendlier, example-bearing message than the domain's for the most common request error.
        if (!request.Weekdays.IsValidSet())
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Weekdays"] = ["At least one weekday is required (e.g. \"Monday, Thursday\")."],
            });

        var profile = await profileRepo.GetAsync(tutorId, ct);
        if (profile is null)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Profile"] = ["Set your time zone first via PUT /profile — series times are defined in it."],
            });

        var student = await studentRepo.GetByIdAsync(request.StudentId, tutorId, ct: ct);
        if (student is null)
            return StudentNotFound();

        var pattern = WeeklyPattern.Create(request.Weekdays, request.StartTimeLocal, request.DurationMinutes, profile.TimeZone);
        if (!pattern.IsSuccess)
            return pattern.ToValidationProblem();

        var created = LessonSeries.Create(
            tutorId, request.StudentId, pattern.Value, request.StartDate, clock.GetUtcNow(),
            request.Title, request.EndDate, request.Price);
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

    /// <summary>Updates a series' metadata (title, price). The schedule is not editable here.</summary>
    public static async Task<Results<Ok<LessonSeriesResponse>, NotFound, ValidationProblem>> UpdateSeries(
        Guid seriesId,
        ClaimsPrincipal principal,
        UpdateLessonSeriesRequest request,
        ILessonSeriesRepository repo,
        IUnitOfWork uow,
        CancellationToken ct)
    {
        var series = await repo.GetByIdAsync(seriesId, principal.GetTelegramId(), track: true, ct);
        if (series is null)
            return TypedResults.NotFound();

        var updated = series.UpdateDetails(
            request.Title ?? series.Title, series.EndDate, request.Price ?? series.Price);
        if (!updated.IsSuccess)
            return updated.ToValidationProblem();

        repo.Update(series);
        await uow.SaveChangesAsync(ct);
        return TypedResults.Ok(LessonSeriesResponse.From(series));
    }

    /// <summary>
    /// Cancels a series effective immediately (in its own time zone): its last possible lesson day
    /// becomes yesterday, so today's virtual occurrence stops expanding along with all future ones.
    /// Today's occurrence that has ALREADY STARTED (or is over) is materialized first, so it survives
    /// as a physical <c>Scheduled</c> row — an in-progress/past lesson is never silently dropped. A
    /// still-upcoming today occurrence (<c>StartUtc &gt; now</c>) is left virtual and correctly dropped.
    /// Any future materialized overrides are removed and reported so the client can notify the user.
    /// Existing materialized rows for today and the past are physical and left untouched.
    /// </summary>
    public static async Task<Results<Ok<CancelSeriesResponse>, NotFound>> CancelSeries(
        Guid seriesId,
        ClaimsPrincipal principal,
        ILessonSeriesRepository seriesRepo,
        ILessonRepository lessonRepo,
        LessonMaterializer materializer,
        IUnitOfWork uow,
        TimeProvider clock,
        CancellationToken ct)
    {
        var tutorId = principal.GetTelegramId();

        var series = await seriesRepo.GetByIdAsync(seriesId, tutorId, track: true, ct);
        if (series is null)
            return TypedResults.NotFound();

        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, series.Pattern.TimeZone).DateTime);

        // Preserve today's already-happened occurrence. Evaluated BEFORE CancelAsOf (GetOccurrences
        // clips to the CURRENT EndDate): a today occurrence that has already started/passed but is
        // still virtual is materialized into a physical Scheduled row so it is not lost when EndDate is
        // tightened to yesterday. A still-upcoming today occurrence is left alone and dropped.
        foreach (var occ in series.GetOccurrences(today, today))
        {
            if (occ.StartUtc > now)
                continue; // Still upcoming today → correctly dropped by CancelAsOf.

            var existing = await lessonRepo.GetBySeriesOccurrenceAsync(seriesId, today, tutorId, track: true, ct);
            if (existing is not null)
                continue; // Already physical → survives untouched.

            var lesson = await materializer.MaterializeSlotAsync(series, occ, ct);
            lessonRepo.Add(lesson);
        }

        series.CancelAsOf(now);

        // Remove future overrides (materialized rows beyond today) — they belong to a schedule that
        // no longer exists. Today's (including the just-materialized) and past rows stay.
        var removed = await lessonRepo.GetMaterializedForSeriesFromAsync(seriesId, tutorId, today.AddDays(1), track: true, ct);
        foreach (var lesson in removed)
            lessonRepo.Remove(lesson);

        seriesRepo.Update(series);
        await uow.SaveChangesAsync(ct);

        return TypedResults.Ok(new CancelSeriesResponse(
            LessonSeriesResponse.From(series), removed.Select(LessonResponse.From).ToList()));
    }

    /// <summary>
    /// Mutates one slot of a series by its original scheduled date, materializing it on demand: if
    /// no physical lesson exists yet, one is instantiated from the series, the patch is applied and
    /// saved — so topics, cancellations and reschedules of single occurrences never need
    /// pre-materialized rows.
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
        var existing = await repo.GetBySeriesOccurrenceAsync(seriesId, occurrenceDate, tutorId, track: true, ct);
        if (existing is not null)
            return ToHttpResult(await patchService.ApplyAsync(existing, request, tutorId, isNew: false, ct: ct));

        // The date must be an actual virtual slot of the series (weekday + date window).
        var slot = series.GetOccurrences(occurrenceDate, occurrenceDate);
        if (slot.Count == 0)
            return TypedResults.NotFound();

        var lesson = await materializer.MaterializeSlotAsync(series, slot[0], ct);
        return ToHttpResult(await patchService.ApplyAsync(
            lesson, request, tutorId, isNew: true,
            excludeOccurrence: new SeriesSlot(seriesId, occurrenceDate), ct: ct));
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

    private static Dictionary<string, string[]>? ValidateRange(DateTimeOffset from, DateTimeOffset to)
    {
        var errors = new Dictionary<string, string[]>();
        if (to <= from)
            errors["To"] = ["'to' must be after 'from'."];
        else if ((to - from).TotalDays > MaxRangeDays)
            errors["To"] = [$"Range must not exceed {MaxRangeDays} days."];

        return errors.Count == 0 ? null : errors;
    }

    // Same message whether the student is missing or belongs to another tutor — existence must not
    // leak across tenants.
    private static ValidationProblem StudentNotFound() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            ["StudentId"] = ["Student not found."],
        });

    private static Conflict<LessonConflictResponse> Conflict(IReadOnlyList<LessonConflict> conflicts) =>
        TypedResults.Conflict(new LessonConflictResponse(
            "The requested time overlaps existing lessons or series.", conflicts));
}
