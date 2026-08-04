using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using StudyScheduler.API.Core.ErrorHandling;
using StudyScheduler.Domain.Lessons;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>
/// HTTP handlers for the Lessons feature: bind the request, hand it to <see cref="LessonService"/>,
/// render the outcome. Every scheduling decision — for single lessons and for series alike — lives in
/// that one service. Wired to routes in <see cref="LessonsModule"/>.
/// Nothing here names the tutor: the request's identity became the scope's tenant in the tenancy
/// middleware, and every read and write below is filtered and stamped by it.
/// </summary>
internal static class Endpoints
{
    private const int MaxRangeDays = 366;

    /// <summary>
    /// Lists the current tutor's lessons intersecting <c>[from, to)</c>: a plain range query over the
    /// rows themselves, series lessons included — a series generates them across the whole planning
    /// horizon, so there is nothing to expand. Reads never write.
    /// </summary>
    public static async Task<Results<Ok<List<LessonResponse>>, ValidationProblem>> GetMine(
        DateTimeOffset from,
        DateTimeOffset to,
        Guid? studentId,
        ILessonRepository lessons,
        CancellationToken ct)
    {
        // The range is a property of the query string, not of the schedule — so it is checked here.
        if (ValidateRange(from, to) is { } errors)
            return TypedResults.ValidationProblem(errors);

        var schedule = await lessons.GetInRangeAsync(from, to, studentId, ct);
        return TypedResults.Ok(schedule.Select(LessonResponse.From).ToList());
    }

    /// <summary>
    /// Returns a single lesson by its id, scoped to the current tutor — one route for one-off lessons
    /// and series occurrences alike, since both are ordinary rows. 404 when the id addresses nothing
    /// of this tutor's.
    /// </summary>
    public static async Task<Results<Ok<LessonResponse>, NotFound>> GetById(
        Guid id,
        LessonService service,
        CancellationToken ct)
    {
        var lesson = await service.GetAsync(id, ct);
        return lesson is null ? TypedResults.NotFound() : TypedResults.Ok(LessonResponse.From(lesson));
    }

    /// <summary>
    /// Creates a lesson from the one create form: a one-off when the request carries no
    /// <c>Repeat</c>, a weekly series when it does — the response says which of the two arrived.
    /// 409 when the requested time collides, either as a single slot or as a weekly one.
    /// </summary>
    public static async Task<Results<Created<CreateLessonResponse>, ValidationProblem, Conflict<LessonConflictResponse>>> Create(
        CreateLessonRequest request,
        LessonService service,
        CancellationToken ct) =>
        ToHttpResult(await service.CreateAsync(request, ct));

    /// <summary>
    /// Partially updates the lesson behind an id, scoped to the current tutor — one route for one-off
    /// lessons and single series occurrences alike.
    /// 404 when the id addresses nothing of this tutor's.
    /// </summary>
    public static async Task<Results<Ok<LessonResponse>, NotFound, ValidationProblem, Conflict<LessonConflictResponse>>> Update(
        Guid id,
        UpdateLessonRequest request,
        LessonService service,
        CancellationToken ct)
    {
        var outcome = await service.UpdateAsync(id, request, ct);
        return outcome is null ? TypedResults.NotFound() : ToHttpResult(outcome);
    }

    /// <summary>
    /// Marks a batch of lessons as paid in one request — what the student's debts screen does when the
    /// tutor is finally handed the money. All or nothing: see
    /// <see cref="LessonService.SettleAsync"/> for what refuses a batch and what is a harmless no-op.
    /// 400 (validation) whenever the selection cannot be honoured as sent; ids of another tutor's
    /// lessons are among those, since the scoped lookup cannot resolve them at all.
    /// </summary>
    public static async Task<Results<Ok<SettleLessonsResponse>, ValidationProblem>> Settle(
        SettleLessonsRequest request,
        LessonService service,
        CancellationToken ct)
    {
        var settled = await service.SettleAsync(request.LessonIds ?? [], ct);
        return settled.IsSuccess
            ? TypedResults.Ok(new SettleLessonsResponse(settled.Value))
            : settled.ToValidationProblem();
    }

    /// <summary>Lists the current tutor's series (active and ended).</summary>
    public static async Task<Ok<List<LessonSeriesResponse>>> GetSeriesList(
        ILessonSeriesRepository repo,
        CancellationToken ct)
    {
        var series = await repo.GetAllAsync(ct);
        return TypedResults.Ok(series.Select(LessonSeriesResponse.From).ToList());
    }

    /// <summary>Returns a single series, scoped to the current tutor.</summary>
    public static async Task<Results<Ok<LessonSeriesResponse>, NotFound>> GetSeriesById(
        Guid seriesId,
        ILessonSeriesRepository repo,
        CancellationToken ct)
    {
        var series = await repo.GetByIdAsync(seriesId, ct: ct);
        return series is null ? TypedResults.NotFound() : TypedResults.Ok(LessonSeriesResponse.From(series));
    }

    /// <summary>
    /// Edits a series in full — name, price, weekly schedule and the date it runs until. See
    /// <see cref="LessonService.UpdateSeriesAsync"/> for what a schedule change does to the lessons
    /// already generated from the previous one, and what <c>keepCustomized</c> decides.
    /// </summary>
    public static async Task<Results<Ok<UpdateSeriesResponse>, NotFound, ValidationProblem, Conflict<LessonConflictResponse>>> UpdateSeries(
        Guid seriesId,
        UpdateLessonSeriesRequest request,
        LessonService service,
        CancellationToken ct)
    {
        var outcome = await service.UpdateSeriesAsync(seriesId, request, ct);
        return outcome switch
        {
            null => TypedResults.NotFound(),
            SeriesUpdateOutcome.Ok ok => TypedResults.Ok(new UpdateSeriesResponse(
                LessonSeriesResponse.From(ok.Change.Series), ToResponses(ok.Change.RemovedLessons))),
            SeriesUpdateOutcome.Validation validation => validation.Failure.ToValidationProblem(),
            SeriesUpdateOutcome.Conflict conflict => Conflict(conflict.Conflicts),
            _ => throw new InvalidOperationException($"Unhandled series outcome '{outcome.GetType().Name}'."),
        };
    }

    /// <summary>
    /// Cancels a series effective immediately — see <see cref="LessonService.CancelSeriesAsync"/> for
    /// what happens to today's occurrence and to the lessons beyond it. The body is optional: sending
    /// none means the default, "keep the occurrences I edited by hand".
    /// </summary>
    public static async Task<Results<Ok<CancelSeriesResponse>, NotFound>> CancelSeries(
        Guid seriesId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CancelLessonSeriesRequest? request,
        LessonService service,
        CancellationToken ct)
    {
        var change = await service.CancelSeriesAsync(
            seriesId, request ?? new CancelLessonSeriesRequest(), ct);
        return change is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new CancelSeriesResponse(
                LessonSeriesResponse.From(change.Series), ToResponses(change.RemovedLessons)));
    }

    /// <summary>Maps the create orchestration's outcome onto the endpoint's HTTP result union.</summary>
    private static Results<Created<CreateLessonResponse>, ValidationProblem, Conflict<LessonConflictResponse>> ToHttpResult(
        CreateLessonOutcome outcome) =>
        outcome switch
        {
            CreateLessonOutcome.LessonCreated created => TypedResults.Created(
                $"/lessons/{created.Lesson.Id}",
                new CreateLessonResponse(LessonResponse.From(created.Lesson), null)),
            CreateLessonOutcome.SeriesCreated created => TypedResults.Created(
                $"/lessons/series/{created.Series.Id}",
                new CreateLessonResponse(null, LessonSeriesResponse.From(created.Series))),
            CreateLessonOutcome.Validation validation => validation.Failure.ToValidationProblem(),
            CreateLessonOutcome.Conflict conflict => Conflict(conflict.Conflicts),
            _ => throw new InvalidOperationException($"Unhandled create outcome '{outcome.GetType().Name}'."),
        };

    /// <summary>Maps the patch pipeline's outcome onto the endpoints' HTTP result union.</summary>
    private static Results<Ok<LessonResponse>, NotFound, ValidationProblem, Conflict<LessonConflictResponse>> ToHttpResult(
        LessonPatchOutcome outcome) =>
        outcome switch
        {
            LessonPatchOutcome.Ok ok => TypedResults.Ok(LessonResponse.From(ok.Lesson)),
            LessonPatchOutcome.Validation validation => validation.Failure.ToValidationProblem(),
            LessonPatchOutcome.Conflict conflict => Conflict(conflict.Conflicts),
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

    private static List<LessonResponse> ToResponses(IReadOnlyList<Lesson> lessons) =>
        lessons.Select(LessonResponse.From).ToList();

    private static Conflict<LessonConflictResponse> Conflict(IReadOnlyList<LessonConflict> conflicts) =>
        TypedResults.Conflict(new LessonConflictResponse(
            "The requested time overlaps existing lessons or series.", conflicts));
}
