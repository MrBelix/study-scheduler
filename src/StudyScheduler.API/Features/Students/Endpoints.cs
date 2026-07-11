using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.API.Core.Authentication;
using StudyScheduler.API.Core.ErrorHandling;
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
        // Name/rate invariants are the domain factory's job.
        var created = Student.Create(
            principal.GetTelegramId(),
            request.Name,
            request.Rate,
            clock.GetUtcNow());
        if (!created.IsSuccess)
            return created.ToValidationProblem();
        var student = created.Value;

        repo.Add(student);
        await uow.SaveChangesAsync(ct);
        return TypedResults.Created($"/students/{student.Id}", StudentResponse.From(student));
    }

    /// <summary>Partially updates a student (including archive via status), scoped to the current tutor.</summary>
    public static async Task<Results<Ok<StudentResponse>, NotFound, ValidationProblem>> Update(
        Guid id,
        ClaimsPrincipal principal,
        UpdateStudentRequest request,
        IStudentRepository repo,
        IUnitOfWork uow,
        CancellationToken ct)
    {
        var tutorId = principal.GetTelegramId();

        var student = await repo.GetByIdAsync(id, tutorId, track: true, ct);
        if (student is null)
            return TypedResults.NotFound();

        // Domain mutators validate the merged fields; failures are collected so one 400 still
        // reports every offending field, before anything is staged for save.
        var errors = new List<Error>();
        errors.AddRange(student.UpdateDetails(
            request.Name ?? student.Name,
            request.Rate ?? student.Rate).Errors);
        if (request.Status is { } status)
            errors.AddRange(student.ChangeStatus(status).Errors);
        if (errors.Count > 0)
            return Result.Failure([.. errors]).ToValidationProblem();

        repo.Update(student);
        await uow.SaveChangesAsync(ct);
        return TypedResults.Ok(StudentResponse.From(student));
    }
}
