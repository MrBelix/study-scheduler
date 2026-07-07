using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.API.Core.Authentication;
using StudyScheduler.API.Core.Time;
using StudyScheduler.Domain.Lessons;
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
        var student = await repo.GetByIdAsync(id, ct);
        if (student is null || student.TutorTelegramId != principal.GetTelegramId())
            return TypedResults.NotFound();

        return TypedResults.Ok(StudentResponse.From(student));
    }

    /// <summary>Creates a student under the current tutor.</summary>
    public static async Task<Results<Created<StudentResponse>, ValidationProblem>> Create(
        ClaimsPrincipal principal,
        CreateStudentRequest request,
        IStudentRepository repo,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (Validate(request.Name, request.Rate, request.TimeZoneId) is { } errors)
            return TypedResults.ValidationProblem(errors);

        var student = Student.Create(
            principal.GetTelegramId(),
            request.Name,
            request.Rate,
            clock.GetUtcNow(),
            request.Subject,
            request.Contact,
            ParseTimeZone(request.TimeZoneId));

        await repo.AddAsync(student, ct);
        return TypedResults.Created($"/students/{student.Id}", StudentResponse.From(student));
    }

    /// <summary>
    /// Partially updates a student (including archive via status), scoped to the current tutor.
    /// Archiving also ends the student's active lesson series: their future virtual slots stop
    /// expanding and stop blocking the tutor's schedule. Physical lessons (past or individually
    /// touched) stay untouched, and un-archiving does not resurrect the ended series.
    /// </summary>
    public static async Task<Results<Ok<StudentResponse>, NotFound, ValidationProblem>> Update(
        Guid id,
        ClaimsPrincipal principal,
        UpdateStudentRequest request,
        IStudentRepository repo,
        ILessonSeriesRepository seriesRepo,
        TimeProvider clock,
        CancellationToken ct)
    {
        var tutorId = principal.GetTelegramId();

        var student = await repo.GetByIdAsync(id, ct);
        if (student is null || student.TutorTelegramId != tutorId)
            return TypedResults.NotFound();

        var name = request.Name ?? student.Name;
        var rate = request.Rate ?? student.Rate;
        if (Validate(name, rate, request.TimeZoneId) is { } errors)
            return TypedResults.ValidationProblem(errors);

        var archiving = request.Status == StudentStatus.Archived && student.Status != StudentStatus.Archived;

        student.UpdateDetails(
            name,
            rate,
            request.Subject ?? student.Subject,
            request.Contact ?? student.Contact,
            request.TimeZoneId is null ? student.TimeZone : ParseTimeZone(request.TimeZoneId));

        if (request.Status is { } status)
            student.ChangeStatus(status);

        await repo.UpdateAsync(student, ct);

        if (archiving)
        {
            foreach (var series in await seriesRepo.GetActiveByStudentAsync(tutorId, student.Id, ct))
            {
                series.EndAsOf(clock.GetUtcNow());
                await seriesRepo.UpdateAsync(series, ct);
            }
        }

        return TypedResults.Ok(StudentResponse.From(student));
    }

    private static Dictionary<string, string[]>? Validate(string? name, decimal rate, string? timeZoneId)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name))
            errors["Name"] = ["Name is required."];
        if (rate < 0)
            errors["Rate"] = ["Rate must be zero or positive."];
        if (!string.IsNullOrWhiteSpace(timeZoneId) && !IanaTimeZone.TryResolve(timeZoneId, out _))
            errors["TimeZoneId"] = ["Unknown time zone."];

        return errors.Count == 0 ? null : errors;
    }

    /// <summary>Assumes the id already passed validation; blank means "no time zone".</summary>
    private static TimeZoneInfo? ParseTimeZone(string? timeZoneId) =>
        IanaTimeZone.TryResolve(timeZoneId, out var timeZone) ? timeZone : null;
}
