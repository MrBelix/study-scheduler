using StudyScheduler.API.Features.Students;
using StudyScheduler.Domain.Students;
using Xunit;

namespace StudyScheduler.Tests;

public class InMemoryStudentRepositoryTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private static Student NewStudent(long tutorId, string name) =>
        Student.Create(tutorId, name, 100m, CreatedAt);

    [Fact]
    public async Task GetAllByTutorId_ReturnsOnlyThatTutorsStudents()
    {
        var repo = new InMemoryStudentRepository();
        await repo.AddAsync(NewStudent(111, "A"));
        await repo.AddAsync(NewStudent(111, "B"));
        await repo.AddAsync(NewStudent(222, "C")); // other tutor — must not leak

        var mine = await repo.GetAllByTutorIdAsync(111);

        Assert.Equal(2, mine.Count);
        Assert.All(mine, s => Assert.Equal(111, s.TutorTelegramId));
    }

    [Fact]
    public async Task GetById_UnknownId_ReturnsNull()
    {
        var repo = new InMemoryStudentRepository();

        var found = await repo.GetByIdAsync(Guid.NewGuid());

        Assert.Null(found);
    }

    [Fact]
    public async Task AddThenGetById_RoundTrips()
    {
        var repo = new InMemoryStudentRepository();
        var student = NewStudent(111, "A");
        await repo.AddAsync(student);

        var found = await repo.GetByIdAsync(student.Id);

        Assert.Same(student, found);
    }

    [Fact]
    public async Task Update_PersistsMutation()
    {
        var repo = new InMemoryStudentRepository();
        var student = NewStudent(111, "A");
        await repo.AddAsync(student);

        student.ChangeStatus(StudentStatus.Archived);
        await repo.UpdateAsync(student);

        var found = await repo.GetByIdAsync(student.Id);
        Assert.Equal(StudentStatus.Archived, found!.Status);
    }
}
