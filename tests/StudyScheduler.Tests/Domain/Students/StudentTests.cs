using StudyScheduler.Domain.Students;
using Xunit;

namespace StudyScheduler.Tests;

public class StudentTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ValidInput_SetsFieldsAndDefaultsToActive()
    {
        var student = Student.Create(555, "  Bob  ", 250m, CreatedAt, subject: " Math ", contact: " @bob ");

        Assert.NotEqual(Guid.Empty, student.Id);
        Assert.Equal(555, student.TutorTelegramId);
        Assert.Equal("Bob", student.Name);
        Assert.Equal(250m, student.Rate);
        Assert.Equal("Math", student.Subject);
        Assert.Equal("@bob", student.Contact);
        Assert.Equal(StudentStatus.Active, student.Status);
        Assert.Equal(CreatedAt, student.CreatedAtUtc);
    }

    [Fact]
    public void Create_BlankSubject_NormalizedToNull()
    {
        var student = Student.Create(555, "Bob", 0m, CreatedAt, subject: "   ");

        Assert.Null(student.Subject);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankName_Throws(string name)
    {
        Assert.Throws<ArgumentException>(() => Student.Create(555, name, 100m, CreatedAt));
    }

    [Fact]
    public void Create_NegativeRate_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Student.Create(555, "Bob", -1m, CreatedAt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Create_NonPositiveTutorId_Throws(long tutorId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Student.Create(tutorId, "Bob", 100m, CreatedAt));
    }

    [Fact]
    public void UpdateDetails_ReplacesEditableFields()
    {
        var student = Student.Create(555, "Bob", 100m, CreatedAt, "Math", "@bob");

        student.UpdateDetails("Alice", 300m, "Physics", null);

        Assert.Equal("Alice", student.Name);
        Assert.Equal(300m, student.Rate);
        Assert.Equal("Physics", student.Subject);
        Assert.Null(student.Contact);
        Assert.Equal(555, student.TutorTelegramId); // ownership never changes
    }

    [Fact]
    public void UpdateDetails_BlankName_Throws()
    {
        var student = Student.Create(555, "Bob", 100m, CreatedAt);

        Assert.Throws<ArgumentException>(() => student.UpdateDetails(" ", 100m, null, null));
    }

    [Fact]
    public void ChangeStatus_Archives()
    {
        var student = Student.Create(555, "Bob", 100m, CreatedAt);

        student.ChangeStatus(StudentStatus.Archived);

        Assert.Equal(StudentStatus.Archived, student.Status);
    }
}
