using StudyScheduler.Domain.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Domain.Lessons;

public class LessonTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StartUtc = new(2026, 7, 6, 15, 0, 0, TimeSpan.Zero);

    /// <summary>A lesson the tutor has already recorded as having happened.</summary>
    private static Lesson CompletedLesson()
    {
        var lesson = Lesson.Create(Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt).Value;
        lesson.ChangeStatus(LessonStatus.Completed);
        return lesson;
    }

    [Fact]
    public void Create_OneOff_SetsDefaultsAndDenormalizesEnd()
    {
        var lesson = Lesson.Create(Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt, topic: "  Algebra  ").Value;

        Assert.Null(lesson.SeriesId);
        Assert.Null(lesson.OccurrenceDate);
        Assert.Equal(StartUtc.AddMinutes(60), lesson.EndUtc);
        Assert.Equal(LessonStatus.Scheduled, lesson.Status);
        Assert.False(lesson.IsPaid);
        Assert.Equal("Algebra", lesson.Topic);
    }

    [Fact]
    public void Create_GeneratedFromSeries_KeepsSlotLink()
    {
        var seriesId = Guid.NewGuid();
        var lesson = Lesson.Create(
            Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt,
            seriesId: seriesId, occurrenceDate: new DateOnly(2026, 7, 6)).Value;

        Assert.Equal(seriesId, lesson.SeriesId);
        Assert.Equal(new DateOnly(2026, 7, 6), lesson.OccurrenceDate);
    }

    [Fact]
    public void Create_SeriesIdWithoutOccurrenceDate_Throws() =>
        Assert.Throws<ArgumentException>(() => Lesson.Create(
            Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt, seriesId: Guid.NewGuid()));

    [Theory]
    [InlineData(10)]
    [InlineData(601)]
    public void Create_DurationOutOfRange_Fails(int duration)
    {
        var result = Lesson.Create(Guid.NewGuid(), StartUtc, duration, 300m, CreatedAt);

        Assert.False(result.IsSuccess);
        Assert.Equal("Lesson.DurationOutOfRange", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Create_TopicTooLong_Fails()
    {
        var result = Lesson.Create(
            Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt, topic: new string('x', 201));

        Assert.False(result.IsSuccess);
        Assert.Equal("Lesson.TopicTooLong", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Create_ZeroPrice_MarksLessonPaid()
    {
        // Act — a free lesson owes nothing, so it must not show up as a debt.
        var lesson = Lesson.Create(Guid.NewGuid(), StartUtc, 60, 0m, CreatedAt).Value;

        // Assert
        Assert.Equal(0m, lesson.Price);
        Assert.True(lesson.IsPaid);
    }

    [Fact]
    public void SetPrice_ToZero_MarksLessonPaid()
    {
        // Arrange
        var lesson = Lesson.Create(Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt).Value;

        // Act
        var result = lesson.SetPrice(0m);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.True(lesson.IsPaid);
    }

    [Fact]
    public void SetPrice_FromZeroToPositive_KeepsPaidFlag()
    {
        // Arrange — created free, hence paid.
        var lesson = Lesson.Create(Guid.NewGuid(), StartUtc, 60, 0m, CreatedAt).Value;

        // Act — a non-zero price never forces the flag either way.
        var result = lesson.SetPrice(300m);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(300m, lesson.Price);
        Assert.True(lesson.IsPaid);
    }

    [Fact]
    public void SetPrice_Negative_Fails()
    {
        // Arrange
        var lesson = Lesson.Create(Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt).Value;

        // Act
        var result = lesson.SetPrice(-1m);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("Lesson.NegativePrice", Assert.Single(result.Errors).Code);
        Assert.Equal(300m, lesson.Price);
    }

    [Fact]
    public void SetPrice_ToZeroOnCancelledLesson_LeavesLessonUnpaid()
    {
        // Arrange — a cancelled lesson, whatever it used to cost.
        var lesson = Lesson.Create(Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt).Value;
        lesson.ChangeStatus(LessonStatus.Cancelled);

        // Act — dropping the price to zero would normally settle the lesson.
        var result = lesson.SetPrice(0m);

        // Assert — but a cancelled lesson can never be paid.
        Assert.True(result.IsSuccess);
        Assert.Equal(0m, lesson.Price);
        Assert.False(lesson.IsPaid);
    }

    [Fact]
    public void SetPaid_AfterZeroPrice_HonoursExplicitFlag()
    {
        // Arrange — free lessons start paid…
        var lesson = Lesson.Create(Guid.NewGuid(), StartUtc, 60, 0m, CreatedAt).Value;

        // Act — …but the tutor can still mark one as unpaid explicitly.
        var result = lesson.SetPaid(false);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.False(lesson.IsPaid);
    }

    [Fact]
    public void SetPaid_TrueOnCancelledLesson_Fails()
    {
        // Arrange
        var lesson = Lesson.Create(Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt).Value;
        lesson.ChangeStatus(LessonStatus.Cancelled);

        // Act
        var result = lesson.SetPaid(true);

        // Assert
        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Lesson.CancelledCannotBePaid", error.Code);
        Assert.Equal("IsPaid", error.Field);
        Assert.False(lesson.IsPaid);
    }

    [Fact]
    public void SetPaid_FalseOnCancelledLesson_Succeeds()
    {
        // Arrange — cancelling already cleared the flag; clearing it again is idempotent.
        var lesson = Lesson.Create(Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt).Value;
        lesson.ChangeStatus(LessonStatus.Cancelled);

        // Act
        var result = lesson.SetPaid(false);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.False(lesson.IsPaid);
    }

    [Fact]
    public void SetPaid_TrueOnCompletedLesson_Succeeds()
    {
        // Arrange — the lesson happened; the student pays for it afterwards.
        var lesson = CompletedLesson();

        // Act
        var result = lesson.SetPaid(true);

        // Assert
        // Settling history is exactly what a completed lesson stays open for — the debt dashboard
        // is fed by these flags.
        Assert.True(result.IsSuccess);
        Assert.True(lesson.IsPaid);
    }

    [Fact]
    public void UpdateTopic_OnCompletedLesson_Succeeds()
    {
        // Arrange
        var lesson = CompletedLesson();

        // Act
        var result = lesson.UpdateTopic("Quadratics");

        // Assert — writing down what the lesson was about is a note, not a change of what happened.
        Assert.True(result.IsSuccess);
        Assert.Equal("Quadratics", lesson.Topic);
    }

    [Fact]
    public void UpdateDescription_OnCompletedLesson_Succeeds()
    {
        // Arrange
        var lesson = CompletedLesson();

        // Act
        var result = lesson.UpdateDescription("Homework: p. 42");

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("Homework: p. 42", lesson.Description);
    }

    [Fact]
    public void Reschedule_MovesStartKeepsDurationAndRecomputesEnd()
    {
        var lesson = Lesson.Create(Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt).Value;
        var newStart = StartUtc.AddDays(1);

        lesson.Reschedule(newStart);

        Assert.Equal(newStart, lesson.StartUtc);
        Assert.Equal(60, lesson.DurationMinutes);
        Assert.Equal(newStart.AddMinutes(60), lesson.EndUtc);
    }

    [Fact]
    public void Reschedule_CompletedLesson_FailsAndLeavesTheTimeAlone()
    {
        // Arrange
        var lesson = CompletedLesson();

        // Act
        var result = lesson.Reschedule(StartUtc.AddDays(1));

        // Assert — it happened when it happened.
        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Lesson.AlreadyCompleted", error.Code);
        Assert.Equal("StartUtc", error.Field);
        Assert.Equal(StartUtc, lesson.StartUtc);
        Assert.Equal(StartUtc.AddMinutes(60), lesson.EndUtc);
    }

    [Fact]
    public void ChangeDuration_KeepsStartAndRecomputesEnd()
    {
        var lesson = Lesson.Create(Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt).Value;

        var result = lesson.ChangeDuration(90);

        Assert.True(result.IsSuccess);
        Assert.Equal(StartUtc, lesson.StartUtc);
        Assert.Equal(90, lesson.DurationMinutes);
        Assert.Equal(StartUtc.AddMinutes(90), lesson.EndUtc);
    }

    [Fact]
    public void ChangeDuration_OutOfRange_Fails()
    {
        var lesson = Lesson.Create(Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt).Value;

        var result = lesson.ChangeDuration(601);

        Assert.False(result.IsSuccess);
        Assert.Equal("Lesson.DurationOutOfRange", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void ChangeDuration_CompletedLesson_FailsAndLeavesTheDurationAlone()
    {
        // Arrange
        var lesson = CompletedLesson();

        // Act
        var result = lesson.ChangeDuration(90);

        // Assert — how long it ran is part of what happened.
        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Lesson.AlreadyCompleted", error.Code);
        Assert.Equal("DurationMinutes", error.Field);
        Assert.Equal(60, lesson.DurationMinutes);
        Assert.Equal(StartUtc.AddMinutes(60), lesson.EndUtc);
    }

    [Fact]
    public void ChangeStatus_UndefinedValue_Fails()
    {
        var lesson = Lesson.Create(Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt).Value;

        var result = lesson.ChangeStatus((LessonStatus)99);

        Assert.False(result.IsSuccess);
        Assert.Equal("Lesson.UnknownStatus", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void ChangeStatus_ToCancelledOnPaidLesson_ClearsPaidFlag()
    {
        // Arrange — the common flow: cancelling a lesson the student had already paid for.
        var lesson = Lesson.Create(Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt).Value;
        lesson.SetPaid(true);

        // Act
        var result = lesson.ChangeStatus(LessonStatus.Cancelled);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(LessonStatus.Cancelled, lesson.Status);
        Assert.False(lesson.IsPaid);
    }

    [Fact]
    public void ChangeStatus_UnCancelling_DoesNotRestorePaidFlag()
    {
        // Arrange — paid, then cancelled (which dropped the flag).
        var lesson = Lesson.Create(Guid.NewGuid(), StartUtc, 60, 300m, CreatedAt).Value;
        lesson.SetPaid(true);
        lesson.ChangeStatus(LessonStatus.Cancelled);

        // Act — the lesson is put back on the schedule.
        var result = lesson.ChangeStatus(LessonStatus.Scheduled);

        // Assert — the payment is not resurrected; the tutor re-states it explicitly.
        Assert.True(result.IsSuccess);
        Assert.Equal(LessonStatus.Scheduled, lesson.Status);
        Assert.False(lesson.IsPaid);
    }

    [Fact]
    public void ChangeStatus_ToCancelledOnCompletedLesson_Fails()
    {
        // Arrange — a lesson recorded as having happened, and paid for.
        var lesson = CompletedLesson();
        lesson.SetPaid(true);

        // Act
        var result = lesson.ChangeStatus(LessonStatus.Cancelled);

        // Assert
        // A completed lesson is a settled fact: cancelling it would quietly erase money the debt
        // dashboard has already counted.
        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Lesson.AlreadyCompleted", error.Code);
        Assert.Equal("Status", error.Field);
        Assert.Equal(LessonStatus.Completed, lesson.Status);
        Assert.True(lesson.IsPaid);
    }

    [Fact]
    public void ChangeStatus_CompletedBackToScheduled_Succeeds()
    {
        // Arrange — the tutor tapped "done" on the wrong lesson.
        var lesson = CompletedLesson();

        // Act
        var result = lesson.ChangeStatus(LessonStatus.Scheduled);

        // Assert — undoing a mistaken settle stays open; only cancelling a completed lesson does not.
        Assert.True(result.IsSuccess);
        Assert.Equal(LessonStatus.Scheduled, lesson.Status);
    }
}
