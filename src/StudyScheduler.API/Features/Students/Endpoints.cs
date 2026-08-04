using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.API.Core.ErrorHandling;
using StudyScheduler.API.Features.Lessons;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Primitives;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Features.Students;

/// <summary>
/// HTTP handlers for the Students feature. Wired to routes in <see cref="StudentsModule"/>.
/// "The current tutor" is the scope's tenant throughout: the repositories read and stamp it, so no
/// handler here has to name an owner.
/// Anything about lessons — the next one of each student, what they still owe, and the schedule an
/// archived student stops having — is asked of <see cref="LessonService"/>, the one façade that owns
/// lesson logic.
/// </summary>
internal static class Endpoints
{
    /// <summary>Lists the active students owned by the current tutor.</summary>
    public static Task<Ok<List<StudentResponse>>> GetMine(
        IStudentRepository repo,
        LessonService lessons,
        CancellationToken ct) =>
        ListByStatus(repo, lessons, StudentStatus.Active, ct);

    /// <summary>Lists the archived students owned by the current tutor — same shape as the active list.</summary>
    public static Task<Ok<List<StudentResponse>>> GetArchived(
        IStudentRepository repo,
        LessonService lessons,
        CancellationToken ct) =>
        ListByStatus(repo, lessons, StudentStatus.Archived, ct);

    /// <summary>
    /// Returns one student with everything its details screen shows — next lesson, still-running
    /// series, lifetime totals and the debt banner — scoped to the current tutor. Any status,
    /// archived included.
    /// The lists deliberately stay slim; only this route serves the fat projection.
    /// </summary>
    public static async Task<Results<Ok<StudentDetailsResponse>, NotFound>> GetById(
        Guid id,
        IStudentRepository repo,
        LessonService lessons,
        ILessonRepository lessonRepo,
        ILessonSeriesRepository seriesRepo,
        TimeProvider clock,
        CancellationToken ct)
    {
        // Another tutor's id is not found rather than forbidden: the read is filtered, so existence
        // never leaks across tenants.
        var student = await repo.GetByIdAsync(id, ct: ct);
        if (student is null)
            return TypedResults.NotFound();

        var nowUtc = clock.GetUtcNow();
        var next = await lessons.GetNextLessonsAsync(student.Id, ct);
        var series = await CurrentSeriesAsync(seriesRepo, student.Id, nowUtc, ct);
        var stats = await lessonRepo.GetStudentStatsAsync(student.Id, ct);
        // Null unless something is actually owed, so the banner is shown exactly when it arrives.
        var debt = await lessons.GetDebtAsync(student.Id, ct);

        return TypedResults.Ok(StudentDetailsResponse.From(
            student, next.GetValueOrDefault(student.Id), series, stats, debt));
    }

    /// <summary>
    /// The lessons behind the debt banner: what the student was taught and never paid for, newest
    /// first, with the totals the banner itself shows. Archived students answer too — the archive
    /// cascade keeps completed lessons precisely because the money outlives the schedule.
    /// 404 only when the id addresses no student of this tutor's; owing nothing is an empty list.
    /// </summary>
    public static async Task<Results<Ok<StudentDebtsResponse>, NotFound>> GetDebts(
        Guid id,
        IStudentRepository repo,
        LessonService lessons,
        CancellationToken ct)
    {
        var student = await repo.GetByIdAsync(id, ct: ct);
        if (student is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(StudentDebtsResponse.From(
            await lessons.GetDebtLessonsAsync(student.Id, ct)));
    }

    /// <summary>Creates a student under the current tutor.</summary>
    public static async Task<Results<Created<StudentResponse>, ValidationProblem>> Create(
        CreateStudentRequest request,
        IStudentRepository repo,
        IUnitOfWork uow,
        TimeProvider clock,
        CancellationToken ct)
    {
        // Name/rate invariants are the domain factory's job; ownership is persistence's.
        var created = Student.Create(
            request.Name,
            request.Rate,
            clock.GetUtcNow());
        if (!created.IsSuccess)
            return created.ToValidationProblem();
        var student = created.Value;

        repo.Add(student);
        await uow.SaveChangesAsync(ct);
        // A brand-new student has neither lessons nor series, so its next lesson is naturally null.
        return TypedResults.Created($"/students/{student.Id}", StudentResponse.From(student));
    }

    /// <summary>
    /// Partially updates a student, scoped to the current tutor. Archiving one (<c>status</c>) is the
    /// tutor saying they stopped teaching them, so it also clears their schedule — see
    /// <see cref="LessonService.StopTeachingAsync"/>. Restoring one is a plain status flip: the
    /// lessons that were swept away are gone, and the tutor books what comes next themselves.
    /// </summary>
    public static async Task<Results<Ok<StudentResponse>, NotFound, ValidationProblem>> Update(
        Guid id,
        UpdateStudentRequest request,
        IStudentRepository repo,
        IUnitOfWork uow,
        LessonService lessons,
        CancellationToken ct)
    {
        var student = await repo.GetByIdAsync(id, track: true, ct);
        if (student is null)
            return TypedResults.NotFound();

        // Domain mutators validate the merged fields; failures are collected so one 400 still
        // reports every offending field, before anything is staged for save.
        var errors = new List<Error>();
        errors.AddRange(student.UpdateDetails(
            request.Name ?? student.Name,
            request.Rate ?? student.Rate).Errors);

        // The cascade runs BEFORE the status flips — and only once the rest of the patch is known to
        // hold — so the schedule is already empty by the time the student reads as archived, and a
        // refused patch never touches it. Archiving is the only transition that costs lessons.
        if (errors.Count == 0 && IsArchiving(student, request.Status))
            await lessons.StopTeachingAsync(student.Id, ct);

        if (request.Status is { } status)
            errors.AddRange(student.ChangeStatus(status).Errors);
        if (errors.Count > 0)
            return Result.Failure([.. errors]).ToValidationProblem();

        repo.Update(student);
        await uow.SaveChangesAsync(ct);

        var next = await lessons.GetNextLessonsAsync(student.Id, ct);
        return TypedResults.Ok(StudentResponse.From(student, next.GetValueOrDefault(student.Id)));
    }

    /// <summary>
    /// Whether this patch is the moment the tutor stops teaching the student. Archiving an already
    /// archived student is not: their schedule was cleared when it first happened.
    /// </summary>
    private static bool IsArchiving(Student student, StudentStatus? requested) =>
        requested is StudentStatus.Archived && student.Status is not StudentStatus.Archived;

    /// <summary>
    /// The tutor's students in one status, each with its next lesson. The status is filtered in the
    /// database, so the client never receives a list it has to filter itself.
    /// </summary>
    private static async Task<Ok<List<StudentResponse>>> ListByStatus(
        IStudentRepository repo,
        LessonService lessons,
        StudentStatus status,
        CancellationToken ct)
    {
        var students = await repo.GetByStatusAsync(status, ct);
        // One bulk pass over the tutor's lessons and series — never a query per student.
        var next = await lessons.GetNextLessonsAsync(ct: ct);

        return TypedResults.Ok(students
            .Select(s => StudentResponse.From(s, next.GetValueOrDefault(s.Id)))
            .ToList());
    }

    /// <summary>
    /// The student's series that can still produce lessons — open-ended, or ending today or later.
    /// The database pre-filter uses a UTC-derived cut-off one day back, a superset for every zone
    /// offset; the exact call is then made per series against its own (the tutor's) time zone, the
    /// same "today" the cancel endpoint works from. Oldest first, as the series list is.
    /// </summary>
    private static async Task<List<LessonSeriesResponse>> CurrentSeriesAsync(
        ILessonSeriesRepository repo,
        Guid studentId,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var cutoff = DateOnly.FromDateTime(nowUtc.UtcDateTime).AddDays(-1);
        var series = await repo.GetActiveAsync(cutoff, ct);

        return series
            .Where(s => s.StudentId == studentId && !HasEnded(s, nowUtc))
            .OrderBy(s => s.CreatedAtUtc)
            .Select(LessonSeriesResponse.From)
            .ToList();
    }

    private static bool HasEnded(LessonSeries series, DateTimeOffset nowUtc) =>
        series.EndDate is { } endDate
        && endDate < DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, series.Pattern.TimeZone).DateTime);
}
