using StudyScheduler.Domain.Students;

namespace StudyScheduler.API.Features.Students;

/// <summary>Request body for creating a student under the current owner.</summary>
public sealed record CreateStudentRequest(string Name);

/// <summary>Student projection returned to the client.</summary>
public sealed record StudentResponse(Guid Id, string Name)
{
    public static StudentResponse From(Student student) => new(student.Id, student.Name);
}
