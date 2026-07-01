using System.Net.Http.Json;

namespace StudyScheduler.IntegrationTests;

/// <summary>
/// End-to-end scope tests over the real stack (SQL Server container + API + Telegram auth):
/// a tutor must never reach another tutor's students. Each test uses distinct tutor ids so the
/// shared database stays isolated between tests.
/// </summary>
[Collection(nameof(AppCollection))]
public class StudentsScopeTests(AppFixture app)
{
    [Fact]
    public async Task Tutor_cannot_access_another_tutors_students()
    {
        var tutorA = TelegramInitData.ForUser(111, "Alice");
        var tutorB = TelegramInitData.ForUser(222, "Bob");

        // Tutor A creates a student.
        var create = await app.Api.PostAs(tutorA, "/students", new { name = "Kid", rate = 100m });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<StudentDto>();
        Assert.NotNull(created);

        // Tutor B must NOT reach it by id — 404 (not 403, so existence isn't leaked).
        var bById = await app.Api.GetAs(tutorB, $"/students/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, bById.StatusCode);

        // Tutor A can.
        var aById = await app.Api.GetAs(tutorA, $"/students/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, aById.StatusCode);

        // Lists are scoped: B sees none of A's students, A sees the one it created.
        var bList = await (await app.Api.GetAs(tutorB, "/students")).Content.ReadFromJsonAsync<List<StudentDto>>();
        Assert.Empty(bList!);

        var aList = await (await app.Api.GetAs(tutorA, "/students")).Content.ReadFromJsonAsync<List<StudentDto>>();
        Assert.Contains(aList!, s => s.Id == created.Id);
    }

    private sealed record StudentDto(Guid Id, string Name);
}
