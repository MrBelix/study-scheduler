using StudyScheduler.Domain.Students;
using Xunit;

namespace StudyScheduler.Tests.Domain.Students;

public class StudentTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ValidInput_SetsFieldsAndDefaultsToActive()
    {
        var student = Student.Create(555, "  Bob  ", 250m, CreatedAt, subject: " Math ", contact: " @bob ").Value;

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
        var student = Student.Create(555, "Bob", 0m, CreatedAt, subject: "   ").Value;

        Assert.Null(student.Subject);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankName_Fails(string name)
    {
        var result = Student.Create(555, name, 100m, CreatedAt);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Name", error.Field);
        Assert.Equal("Student.NameRequired", error.Code);
    }

    [Fact]
    public void Create_NegativeRate_Fails()
    {
        var result = Student.Create(555, "Bob", -1m, CreatedAt);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Rate", error.Field);
        Assert.Equal("Student.NegativeRate", error.Code);
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
        var student = Student.Create(555, "Bob", 100m, CreatedAt, "Math", "@bob").Value;

        var result = student.UpdateDetails("Alice", 300m, "Physics", null, TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv"));

        Assert.True(result.IsSuccess);
        Assert.Equal("Alice", student.Name);
        Assert.Equal(300m, student.Rate);
        Assert.Equal("Physics", student.Subject);
        Assert.Null(student.Contact);
        Assert.Equal("Europe/Kyiv", student.TimeZone?.Id);
        Assert.Equal(555, student.TutorTelegramId); // ownership never changes
    }

    [Fact]
    public void UpdateDetails_BlankName_FailsWithoutMutating()
    {
        var student = Student.Create(555, "Bob", 100m, CreatedAt).Value;

        var result = student.UpdateDetails(" ", 100m, null, null, null);

        Assert.False(result.IsSuccess);
        Assert.Equal("Name", Assert.Single(result.Errors).Field);
        Assert.Equal("Bob", student.Name);
    }

    [Fact]
    public void Create_WithoutTimeZone_LeavesItNull()
    {
        var student = Student.Create(555, "Bob", 100m, CreatedAt).Value;

        Assert.Null(student.TimeZone);
    }

    [Fact]
    public void ChangeStatus_Archives()
    {
        var student = Student.Create(555, "Bob", 100m, CreatedAt).Value;

        var result = student.ChangeStatus(StudentStatus.Archived);

        Assert.True(result.IsSuccess);
        Assert.Equal(StudentStatus.Archived, student.Status);
    }

    [Fact]
    public void ChangeStatus_UndefinedValue_Fails()
    {
        var student = Student.Create(555, "Bob", 100m, CreatedAt).Value;

        var result = student.ChangeStatus((StudentStatus)99);

        Assert.False(result.IsSuccess);
        Assert.Equal("Status", Assert.Single(result.Errors).Field);
        Assert.Equal(StudentStatus.Active, student.Status);
    }
}
