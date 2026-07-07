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
        var lesson = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt, topic: "  Algebra  ");

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
        var lesson = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt, topic: "   ");

        Assert.Null(lesson.Topic);
    }

    [Fact]
    public void Create_WithDescription_TrimsAndStores()
    {
        var lesson = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt, description: "  Chapter 4, ex. 12–20  ");

        Assert.Equal("Chapter 4, ex. 12–20", lesson.Description);
    }

    [Fact]
    public void Create_TopicTooLong_Throws()
    {
        var topic = new string('x', Lesson.MaxTopicLength + 1);

        Assert.Throws<ArgumentException>(
            () => Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt, topic: topic));
    }

    [Fact]
    public void Create_DescriptionTooLong_Throws()
    {
        var description = new string('x', Lesson.MaxDescriptionLength + 1);

        Assert.Throws<ArgumentException>(
            () => Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt, description: description));
    }

    [Fact]
    public void UpdateDescription_BlankValue_NormalizedToNull()
    {
        var lesson = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt, description: "notes");

        lesson.UpdateDescription("   ");

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
    public void Create_DurationOutOfRange_Throws(int duration)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Lesson.Create(555, StudentId, Start, duration, 250m, CreatedAt));
    }

    [Fact]
    public void Create_NegativePrice_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Lesson.Create(555, StudentId, Start, 60, -1m, CreatedAt));
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

        var lesson = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt, seriesId: seriesId, occurrenceDate: date);

        Assert.Equal(seriesId, lesson.SeriesId);
        Assert.Equal(date, lesson.OccurrenceDate);
    }

    [Fact]
    public void Reschedule_NewStartAndDuration_RecomputesEndUtc()
    {
        var lesson = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt);
        var newStart = Start.AddDays(1);

        lesson.Reschedule(newStart, 90);

        Assert.Equal(newStart, lesson.StartUtc);
        Assert.Equal(90, lesson.DurationMinutes);
        Assert.Equal(newStart.AddMinutes(90), lesson.EndUtc);
    }

    [Fact]
    public void Reschedule_InvalidDuration_Throws()
    {
        var lesson = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt);

        Assert.Throws<ArgumentOutOfRangeException>(() => lesson.Reschedule(Start, 5));
    }

    [Fact]
    public void SetPrice_Negative_Throws()
    {
        var lesson = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt);

        Assert.Throws<ArgumentOutOfRangeException>(() => lesson.SetPrice(-10m));
    }

    [Fact]
    public void ChangeStatus_And_SetPaid_Apply()
    {
        var lesson = Lesson.Create(555, StudentId, Start, 60, 250m, CreatedAt);

        lesson.ChangeStatus(LessonStatus.Completed);
        lesson.SetPaid(true);

        Assert.Equal(LessonStatus.Completed, lesson.Status);
        Assert.True(lesson.IsPaid);
    }
}
