using StudyScheduler.Domain.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Domain.Lessons;

public class LessonTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StartUtc = new(2026, 7, 6, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_OneOff_SetsDefaultsAndDenormalizesEnd()
    {
        var lesson = Lesson.Create(555, Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt, topic: "  Algebra  ").Value;

        Assert.Null(lesson.SeriesId);
        Assert.Null(lesson.OccurrenceDate);
        Assert.Equal(StartUtc.AddMinutes(60), lesson.EndUtc);
        Assert.Equal(LessonStatus.Scheduled, lesson.Status);
        Assert.False(lesson.IsPaid);
        Assert.Equal("Algebra", lesson.Topic);
    }

    [Fact]
    public void Create_MaterializedFromSeries_KeepsSlotLink()
    {
        var seriesId = Guid.NewGuid();
        var lesson = Lesson.Create(
            555, Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt,
            seriesId: seriesId, occurrenceDate: new DateOnly(2026, 7, 6)).Value;

        Assert.Equal(seriesId, lesson.SeriesId);
        Assert.Equal(new DateOnly(2026, 7, 6), lesson.OccurrenceDate);
    }

    [Fact]
    public void Create_SeriesIdWithoutOccurrenceDate_Throws() =>
        Assert.Throws<ArgumentException>(() => Lesson.Create(
            555, Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt, seriesId: Guid.NewGuid()));

    [Theory]
    [InlineData(10)]
    [InlineData(601)]
    public void Create_DurationOutOfRange_Fails(int duration)
    {
        var result = Lesson.Create(555, Guid.NewGuid(), StartUtc, duration, 300m, CreatedAt);

        Assert.False(result.IsSuccess);
        Assert.Equal("Lesson.DurationOutOfRange", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Create_TopicTooLong_Fails()
    {
        var result = Lesson.Create(
            555, Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt, topic: new string('x', 201));

        Assert.False(result.IsSuccess);
        Assert.Equal("Lesson.TopicTooLong", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Reschedule_MovesStartKeepsDurationAndRecomputesEnd()
    {
        var lesson = Lesson.Create(555, Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt).Value;
        var newStart = StartUtc.AddDays(1);

        lesson.Reschedule(newStart);

        Assert.Equal(newStart, lesson.StartUtc);
        Assert.Equal(60, lesson.DurationMinutes);
        Assert.Equal(newStart.AddMinutes(60), lesson.EndUtc);
    }

    [Fact]
    public void ChangeDuration_KeepsStartAndRecomputesEnd()
    {
        var lesson = Lesson.Create(555, Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt).Value;

        var result = lesson.ChangeDuration(90);

        Assert.True(result.IsSuccess);
        Assert.Equal(StartUtc, lesson.StartUtc);
        Assert.Equal(90, lesson.DurationMinutes);
        Assert.Equal(StartUtc.AddMinutes(90), lesson.EndUtc);
    }

    [Fact]
    public void ChangeDuration_OutOfRange_Fails()
    {
        var lesson = Lesson.Create(555, Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt).Value;

        var result = lesson.ChangeDuration(601);

        Assert.False(result.IsSuccess);
        Assert.Equal("Lesson.DurationOutOfRange", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void ChangeStatus_UndefinedValue_Fails()
    {
        var lesson = Lesson.Create(555, Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt).Value;

        var result = lesson.ChangeStatus((LessonStatus)99);

        Assert.False(result.IsSuccess);
        Assert.Equal("Lesson.UnknownStatus", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Create_FreshLesson_HasNoNotificationsSent()
    {
        // Act
        var lesson = Lesson.Create(555, Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt).Value;

        // Assert
        Assert.Equal(NotificationState.None, lesson.Notifications);
        Assert.False(lesson.Notifications.IsReminderSent);
        Assert.False(lesson.Notifications.IsFollowUpSent);
    }

    [Fact]
    public void MarkReminderSent_FreshLesson_SetsReminderState()
    {
        // Arrange
        var lesson = Lesson.Create(555, Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt).Value;
        var sentAt = StartUtc.AddMinutes(-30);

        // Act
        lesson.MarkReminderSent(sentAt);

        // Assert
        Assert.True(lesson.Notifications.IsReminderSent);
        Assert.Equal(sentAt, lesson.Notifications.ReminderSentAtUtc);
        Assert.False(lesson.Notifications.IsFollowUpSent);
    }

    [Fact]
    public void MarkFollowUpSent_FreshLesson_SetsFollowUpState()
    {
        // Arrange
        var lesson = Lesson.Create(555, Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt).Value;
        var sentAt = StartUtc.AddMinutes(60);

        // Act
        lesson.MarkFollowUpSent(sentAt);

        // Assert
        Assert.True(lesson.Notifications.IsFollowUpSent);
        Assert.Equal(sentAt, lesson.Notifications.FollowUpSentAtUtc);
        Assert.False(lesson.Notifications.IsReminderSent);
    }
}
