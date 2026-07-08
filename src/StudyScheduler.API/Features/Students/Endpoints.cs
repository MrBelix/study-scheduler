using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.API.Core.Authentication;
using StudyScheduler.API.Core.ErrorHandling;
using StudyScheduler.API.Core.Time;
using StudyScheduler.Domain.Lessons;
using StudyScheduler.Domain.Primitives;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Features.Students;

/// <summary>HTTP handlers for the Students feature. Wired to routes in <see cref="StudentsModule"/>.</summary>
internal static class Endpoints
{
    /// <summary>Lists the students owned by the current tutor.</summary>
    public static async Task<Ok<List<StudentResponse>>> GetMine(
        ClaimsPrincipal principal,
        IStudentRepository repo,
        CancellationToken ct)
    {
        var students = await repo.GetAllByTutorIdAsync(principal.GetTelegramId(), ct);
        return TypedResults.Ok(students.Select(StudentResponse.From).ToList());
    }

    /// <summary>Returns a single student, scoped to the current tutor.</summary>
    public static async Task<Results<Ok<StudentResponse>, NotFound>> GetById(
        Guid id,
        ClaimsPrincipal principal,
        IStudentRepository repo,
        CancellationToken ct)
    {
        var student = await repo.GetByIdAsync(id, principal.GetTelegramId(), ct: ct);
        if (student is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(StudentResponse.From(student));
    }

    /// <summary>Creates a student under the current tutor.</summary>
    public static async Task<Results<Created<StudentResponse>, ValidationProblem>> Create(
        ClaimsPrincipal principal,
        CreateStudentRequest request,
        IStudentRepository repo,
        IUnitOfWork uow,
        TimeProvider clock,
        CancellationToken ct)
    {
        // The time zone id is an HTTP-contract concern (the domain receives a resolved
        // TimeZoneInfo); name/rate invariants are the domain factory's job.
        if (ValidateTimeZoneId(request.TimeZoneId) is { } errors)
            return TypedResults.ValidationProblem(errors);

        var created = Student.Create(
            principal.GetTelegramId(),
            request.Name,
            request.Rate,
            clock.GetUtcNow(),
            request.Subject,
            request.Contact,
            ParseTimeZone(request.TimeZoneId));
        if (!created.IsSuccess)
            return created.ToValidationProblem();
        var student = created.Value;

        repo.Add(student);
        await uow.SaveChangesAsync(ct);
        return TypedResults.Created($"/students/{student.Id}", StudentResponse.From(student));
    }

    /// <summary>
    /// Partially updates a student (including archive via status), scoped to the current tutor.
    /// Archiving also ends the student's active lesson series: their future virtual slots stop
    /// expanding and stop blocking the tutor's schedule. The archive and the series endings
    /// commit as one unit — a failure can't leave an archived student with live series.
    /// Physical lessons (past or individually touched) stay untouched, and un-archiving does
    /// not resurrect the ended series.
    /// </summary>
    public static async Task<Results<Ok<StudentResponse>, NotFound, ValidationProblem>> Update(
        Guid id,
        ClaimsPrincipal principal,
        UpdateStudentRequest request,
        IStudentRepository repo,
        ILessonSeriesRepository seriesRepo,
        IUnitOfWork uow,
        TimeProvider clock,
        CancellationToken ct)
    {
        var tutorId = principal.GetTelegramId();

        var student = await repo.GetByIdAsync(id, tutorId, track: true, ct);
        if (student is null)
            return TypedResults.NotFound();

        if (ValidateTimeZoneId(request.TimeZoneId) is { } timeZoneErrors)
            return TypedResults.ValidationProblem(timeZoneErrors);

        var archiving = request.Status == StudentStatus.Archived && student.Status != StudentStatus.Archived;

        // Domain mutators validate the merged fields; failures are collected so one 400 still
        // reports every offending field, before anything is staged for save.
        var errors = new List<Error>();
        errors.AddRange(student.UpdateDetails(
            request.Name ?? student.Name,
            request.Rate ?? student.Rate,
            request.Subject ?? student.Subject,
            request.Contact ?? student.Contact,
            request.TimeZoneId is null ? student.TimeZone : ParseTimeZone(request.TimeZoneId)).Errors);
        if (request.Status is { } status)
            errors.AddRange(student.ChangeStatus(status).Errors);
        if (errors.Count > 0)
            return Result.Failure([.. errors]).ToValidationProblem();

        repo.Update(student);

        if (archiving)
        {
            foreach (var series in await seriesRepo.GetActiveByStudentAsync(tutorId, student.Id, ct))
            {
                series.EndAsOf(clock.GetUtcNow());
                seriesRepo.Update(series);
            }
        }

        await uow.SaveChangesAsync(ct);
        return TypedResults.Ok(StudentResponse.From(student));
    }

    private static Dictionary<string, string[]>? ValidateTimeZoneId(string? timeZoneId) =>
        !string.IsNullOrWhiteSpace(timeZoneId) && !IanaTimeZone.TryResolve(timeZoneId, out _)
            ? new Dictionary<string, string[]> { ["TimeZoneId"] = ["Unknown time zone."] }
            : null;

    /// <summary>Assumes the id already passed validation; blank means "no time zone".</summary>
    private static TimeZoneInfo? ParseTimeZone(string? timeZoneId) =>
        IanaTimeZone.TryResolve(timeZoneId, out var timeZone) ? timeZone : null;
}
