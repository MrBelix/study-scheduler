using StudyScheduler.Domain.Students;
using StudyScheduler.Tests.Core.Tenancy;
using Xunit;

namespace StudyScheduler.Tests.Domain.Students;

public class StudentTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ValidInput_SetsFieldsAndDefaultsToActive()
    {
        var student = Student.Create("  Bob  ", 250m, CreatedAt).Value;

        Assert.NotEqual(Guid.Empty, student.Id);
        // Ownership is not the factory's to give: persistence stamps the scope's tutor on insert.
        Assert.Equal(0, student.TutorTelegramId);
        Assert.Equal("Bob", student.Name);
        Assert.Equal(250m, student.Rate);
        Assert.Equal(StudentStatus.Active, student.Status);
        Assert.Equal(CreatedAt, student.CreatedAtUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankName_Fails(string name)
    {
        var result = Student.Create(name, 100m, CreatedAt);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Name", error.Field);
        Assert.Equal("Student.NameRequired", error.Code);
    }

    [Fact]
    public void Create_NegativeRate_Fails()
    {
        var result = Student.Create("Bob", -1m, CreatedAt);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Rate", error.Field);
        Assert.Equal("Student.NegativeRate", error.Code);
    }

    [Fact]
    public void UpdateDetails_ReplacesEditableFields()
    {
        // Stamped as persistence would stamp it, so the ownership assertion below is about a row that
        // actually has an owner.
        var student = Student.Create("Bob", 100m, CreatedAt).Value.OwnedBy(555);

        var result = student.UpdateDetails("Alice", 300m);

        Assert.True(result.IsSuccess);
        Assert.Equal("Alice", student.Name);
        Assert.Equal(300m, student.Rate);
        Assert.Equal(555, student.TutorTelegramId); // ownership never changes
    }

    [Fact]
    public void UpdateDetails_BlankName_FailsWithoutMutating()
    {
        var student = Student.Create("Bob", 100m, CreatedAt).Value;

        var result = student.UpdateDetails(" ", 100m);

        Assert.False(result.IsSuccess);
        Assert.Equal("Name", Assert.Single(result.Errors).Field);
        Assert.Equal("Bob", student.Name);
    }

    [Fact]
    public void ChangeStatus_Archives()
    {
        var student = Student.Create("Bob", 100m, CreatedAt).Value;

        var result = student.ChangeStatus(StudentStatus.Archived);

        Assert.True(result.IsSuccess);
        Assert.Equal(StudentStatus.Archived, student.Status);
    }

    [Fact]
    public void ChangeStatus_UndefinedValue_Fails()
    {
        var student = Student.Create("Bob", 100m, CreatedAt).Value;

        var result = student.ChangeStatus((StudentStatus)99);

        Assert.False(result.IsSuccess);
        Assert.Equal("Status", Assert.Single(result.Errors).Field);
        Assert.Equal(StudentStatus.Active, student.Status);
    }
}
