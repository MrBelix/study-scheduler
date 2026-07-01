using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using StudyScheduler.API.Core.Authentication;
using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Features.Students;

public static class Endpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/students").RequireAuthorization();

        group.MapGet("/", GetMine);
        group.MapGet("/{id:guid}", GetById);
        group.MapPost("/", Create);
        group.MapPatch("/{id:guid}", Update);
    }

    /// <summary>Lists the students owned by the current tutor.</summary>
    private static async Task<Ok<List<StudentResponse>>> GetMine(
        ClaimsPrincipal principal,
        IStudentRepository repo)
    {
        var students = await repo.GetAllByTutorIdAsync(principal.GetTelegramId());
        return TypedResults.Ok(students.Select(StudentResponse.From).ToList());
    }

    /// <summary>Returns a single student, scoped to the current tutor.</summary>
    private static async Task<Results<Ok<StudentResponse>, NotFound>> GetById(
        Guid id,
        ClaimsPrincipal principal,
        IStudentRepository repo)
    {
        var student = await repo.GetByIdAsync(id);
        if (student is null || student.TutorTelegramId != principal.GetTelegramId())
            return TypedResults.NotFound();

        return TypedResults.Ok(StudentResponse.From(student));
    }

    /// <summary>Creates a student under the current tutor.</summary>
    private static async Task<Results<Created<StudentResponse>, ValidationProblem>> Create(
        ClaimsPrincipal principal,
        CreateStudentRequest request,
        IStudentRepository repo,
        TimeProvider clock)
    {
        if (Validate(request.Name, request.Rate) is { } errors)
            return TypedResults.ValidationProblem(errors);

        var student = Student.Create(
            principal.GetTelegramId(),
            request.Name,
            request.Rate,
            clock.GetUtcNow(),
            request.Subject,
            request.Contact);

        await repo.AddAsync(student);
        return TypedResults.Created($"/students/{student.Id}", StudentResponse.From(student));
    }

    /// <summary>Partially updates a student (including archive via status), scoped to the current tutor.</summary>
    private static async Task<Results<Ok<StudentResponse>, NotFound, ValidationProblem>> Update(
        Guid id,
        ClaimsPrincipal principal,
        UpdateStudentRequest request,
        IStudentRepository repo)
    {
        var student = await repo.GetByIdAsync(id);
        if (student is null || student.TutorTelegramId != principal.GetTelegramId())
            return TypedResults.NotFound();

        var name = request.Name ?? student.Name;
        var rate = request.Rate ?? student.Rate;
        if (Validate(name, rate) is { } errors)
            return TypedResults.ValidationProblem(errors);

        student.UpdateDetails(
            name,
            rate,
            request.Subject ?? student.Subject,
            request.Contact ?? student.Contact);

        if (request.Status is { } status)
            student.ChangeStatus(status);

        await repo.UpdateAsync(student);
        return TypedResults.Ok(StudentResponse.From(student));
    }

    private static Dictionary<string, string[]>? Validate(string? name, decimal rate)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name))
            errors["Name"] = ["Name is required."];
        if (rate < 0)
            errors["Rate"] = ["Rate must be zero or positive."];

        return errors.Count == 0 ? null : errors;
    }
}
