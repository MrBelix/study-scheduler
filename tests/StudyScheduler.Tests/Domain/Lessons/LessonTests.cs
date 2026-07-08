using StudyScheduler.Domain.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Domain.Lessons;

public class LessonTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Start = new(2026, 7, 6, 14, 0, 0, TimeSpan.Zero);
    private static readonly Guid StudentId = Guid.NewGuid();

    [Fact]
    public void Create_ValidInput_SetsScheduledStatusAndComputesEndUtc()
    {
        var lesson = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt, topic: "  Algebra  ").Value;

        Assert.NotEqual(Guid.Empty, lesson.Id);
        Assert.Equal(555, lesson.TutorTelegramId);
        Assert.Equal(StudentId, lesson.StudentId);
        Assert.Equal(Start, lesson.StartUtc);
        Assert.Equal(Start.AddMinutes(60), lesson.EndUtc);
        Assert.Equal(60, lesson.DurationMinutes);
        Assert.Equal(250m, lesson.Price);
        Assert.Equal("Algebra", lesson.Topic);
        Assert.Equal(LessonStatus.Scheduled, lesson.Status);
        Assert.False(lesson.IsPaid);
        Assert.Null(lesson.SeriesId);
        Assert.Null(lesson.OccurrenceDate);
        Assert.Equal(CreatedAt, lesson.CreatedAtUtc);
    }

    [Fact]
    public void Create_BlankTopic_NormalizedToNull()
    {
        var lesson = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt, topic: "   ").Value;

        Assert.Null(lesson.Topic);
    }

    [Fact]
    public void Create_WithDescription_TrimsAndStores()
    {
        var lesson = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt, description: "  Chapter 4, ex. 12–20  ").Value;

        Assert.Equal("Chapter 4, ex. 12–20", lesson.Description);
    }

    [Fact]
    public void Create_TopicTooLong_Fails()
    {
        var topic = new string('x', Lesson.MaxTopicLength + 1);

        var result = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt, topic: topic);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Topic", error.Field);
        Assert.Equal("Lesson.TopicTooLong", error.Code);
    }

    [Fact]
    public void Create_DescriptionTooLong_Fails()
    {
        var description = new string('x', Lesson.MaxDescriptionLength + 1);

        var result = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt, description: description);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Description", error.Field);
        Assert.Equal("Lesson.DescriptionTooLong", error.Code);
    }

    [Fact]
    public void Create_MultipleInvalidFields_ReportsAllErrors()
    {
        var result = Lesson.Create(555, StudentId, Start, 5, -1m, CreatedAt);

        Assert.False(result.IsSuccess);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.Field == "DurationMinutes");
        Assert.Contains(result.Errors, e => e.Field == "Price");
    }

    [Fact]
    public void UpdateDescription_BlankValue_NormalizedToNull()
    {
        var lesson = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt, description: "notes").Value;

        var result = lesson.UpdateDescription("   ");

        Assert.True(result.IsSuccess);
        Assert.Null(lesson.Description);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Create_NonPositiveTutorId_Throws(long tutorId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Lesson.Create(tutorId, StudentId, Start, 60, 250m, CreatedAt));
    }

    [Fact]
    public void Create_EmptyStudentId_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => Lesson.Create(555, Guid.Empty, Start, 60, 250m, CreatedAt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(601)]
    [InlineData(-30)]
    public void Create_DurationOutOfRange_Fails(int duration)
    {
        var result = Lesson.Create(555, StudentId, Start, duration, 250m, CreatedAt);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("DurationMinutes", error.Field);
        Assert.Equal("Lesson.DurationOutOfRange", error.Code);
    }

    [Fact]
    public void Create_NegativePrice_Fails()
    {
        var result = Lesson.Create(555, StudentId, Start, 60, -1m, CreatedAt);

        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Price", error.Field);
        Assert.Equal("Lesson.NegativePrice", error.Code);
    }

    [Fact]
    public void Create_SeriesIdWithoutOccurrenceDate_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt, seriesId: Guid.NewGuid()));
    }

    [Fact]
    public void Create_OccurrenceDateWithoutSeriesId_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt, occurrenceDate: new DateOnly(2026, 7, 6)));
    }

    [Fact]
    public void Create_ForSeries_KeepsSeriesIdAndOccurrenceDate()
    {
        var seriesId = Guid.NewGuid();
        var date = new DateOnly(2026, 7, 6);

        var lesson = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt, seriesId: seriesId, occurrenceDate: date).Value;

        Assert.Equal(seriesId, lesson.SeriesId);
        Assert.Equal(date, lesson.OccurrenceDate);
    }

    [Fact]
    public void Reschedule_NewStartAndDuration_RecomputesEndUtc()
    {
        var lesson = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt).Value;
        var newStart = Start.AddDays(1);

        var result = lesson.Reschedule(newStart, 90);

        Assert.True(result.IsSuccess);
        Assert.Equal(newStart, lesson.StartUtc);
        Assert.Equal(90, lesson.DurationMinutes);
        Assert.Equal(newStart.AddMinutes(90), lesson.EndUtc);
    }

    [Fact]
    public void Reschedule_InvalidDuration_FailsWithoutMutating()
    {
        var lesson = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt).Value;

        var result = lesson.Reschedule(Start.AddDays(1), 5);

        Assert.False(result.IsSuccess);
        Assert.Equal("DurationMinutes", Assert.Single(result.Errors).Field);
        Assert.Equal(Start, lesson.StartUtc);
        Assert.Equal(60, lesson.DurationMinutes);
    }

    [Fact]
    public void SetPrice_Negative_FailsWithoutMutating()
    {
        var lesson = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt).Value;

        var result = lesson.SetPrice(-10m);

        Assert.False(result.IsSuccess);
        Assert.Equal("Price", Assert.Single(result.Errors).Field);
        Assert.Equal(250m, lesson.Price);
    }

    [Fact]
    public void ChangeStatus_UndefinedValue_Fails()
    {
        var lesson = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt).Value;

        var result = lesson.ChangeStatus((LessonStatus)99);

        Assert.False(result.IsSuccess);
        Assert.Equal("Status", Assert.Single(result.Errors).Field);
        Assert.Equal(LessonStatus.Scheduled, lesson.Status);
    }

    [Fact]
    public void ChangeStatus_And_SetPaid_Apply()
    {
        var lesson = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt).Value;

        var result = lesson.ChangeStatus(LessonStatus.Completed);
        lesson.SetPaid(true);

        Assert.True(result.IsSuccess);
        Assert.Equal(LessonStatus.Completed, lesson.Status);
        Assert.True(lesson.IsPaid);
    }
}
