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
        group.MapPost("/", Create);
    }

    /// <summary>Lists the students owned by the current Telegram user.</summary>
    private static async Task<Ok<List<StudentResponse>>> GetMine(
        ClaimsPrincipal principal,
        IStudentRepository repo)
    {
        var ownerId = principal.GetTelegramId();
        var students = await repo.GetAllByOwnerIdAsync(ownerId);
        return TypedResults.Ok(students.Select(StudentResponse.From).ToList());
    }

    /// <summary>Creates a student under the current Telegram user.</summary>
    private static async Task<Results<Created<StudentResponse>, ValidationProblem>> Create(
        ClaimsPrincipal principal,
        CreateStudentRequest request,
        IStudentRepository repo)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Name)] = ["Name is required."],
            });
        }

        var student = Student.Create(principal.GetTelegramId(), request.Name);
        await repo.AddAsync(student);

        var response = StudentResponse.From(student);
        return TypedResults.Created($"/students/{student.Id}", response);
    }
}
