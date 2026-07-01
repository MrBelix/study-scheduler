using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Features.Students;

/// <summary>Request body for creating a student under the current tutor.</summary>
public sealed record CreateStudentRequest(
    string Name,
    decimal Rate,
    string? Subject,
    string? Contact);

/// <summary>
/// Partial update — only non-null fields are applied. <c>Rate</c> is nullable so "not provided"
/// is distinguishable from 0.
/// </summary>
public sealed record UpdateStudentRequest(
    string? Name,
    decimal? Rate,
    string? Subject,
    string? Contact,
    StudentStatus? Status);

/// <summary>Student projection returned to the client.</summary>
public sealed record StudentResponse(
    Guid Id,
    string Name,
    decimal Rate,
    string? Subject,
    string? Contact,
    StudentStatus Status,
    DateTimeOffset CreatedAtUtc)
{
    public static StudentResponse From(Student student) => new(
        student.Id,
        student.Name,
        student.Rate,
        student.Subject,
        student.Contact,
        student.Status,
        student.CreatedAtUtc);
}
