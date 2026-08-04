using StudyScheduler.API.Features.Reports;
using StudyScheduler.Domain.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Features.Reports;

/// <summary>
/// Drives the pure aggregation seam directly — the calculator's own query needs a database, so the
/// arithmetic is exercised on hand-built <see cref="Lesson"/> lists.
/// </summary>
public class ReportSummaryCalculatorTests
{
    private static readonly Guid Student = Guid.NewGuid();
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private static Lesson Entry(
        DateTimeOffset startUtc,
        LessonStatus status = LessonStatus.Scheduled,
        decimal price = 100m,
        bool isPaid = false,
        Guid? seriesId = null)
    {
        var lesson = Lesson.Create(
            Student, startUtc, 60, price, CreatedAt,
            seriesId: seriesId,
            occurrenceDate: seriesId is null ? null : DateOnly.FromDateTime(startUtc.UtcDateTime)).Value;
        if (status != LessonStatus.Scheduled)
            lesson.ChangeStatus(status);
        // After the status: cancelling clears the payment flag, so the order is not interchangeable.
        lesson.SetPaid(isPaid);
        return lesson;
    }

    [Fact]
    public void Summarize_EmptySchedule_ReturnsZeros()
    {
        // Arrange
        IReadOnlyList<Lesson> schedule = [];

        // Act
        var summary = ReportSummaryCalculator.Summarize(schedule, Now);

        // Assert
        Assert.Equal(0, summary.CompletedCount);
        Assert.Equal(0, summary.CancelledCount);
        Assert.Equal(0, summary.UpcomingCount);
        Assert.Equal(0, summary.UnclosedCount);
        Assert.Equal(0m, summary.PlannedIncome);
        Assert.Equal(0m, summary.ActualIncome);
    }

    [Fact]
    public void Summarize_MixedStatuses_CountsEachBucket()
    {
        // Arrange
        IReadOnlyList<Lesson> schedule =
        [
            Entry(Now.AddDays(-2), LessonStatus.Completed, isPaid: true),
            Entry(Now.AddDays(-1), LessonStatus.Cancelled),
            Entry(Now.AddDays(-1), LessonStatus.Scheduled),   // past, still open
            Entry(Now.AddDays(1), LessonStatus.Scheduled),    // upcoming
            Entry(Now.AddDays(2), LessonStatus.Scheduled),    // upcoming
        ];

        // Act
        var summary = ReportSummaryCalculator.Summarize(schedule, Now);

        // Assert
        Assert.Equal(1, summary.CompletedCount);
        Assert.Equal(1, summary.CancelledCount);
        Assert.Equal(2, summary.UpcomingCount);
        Assert.Equal(1, summary.UnclosedCount);
    }

    [Fact]
    public void Summarize_PastScheduledLesson_CountsAsUnclosed()
    {
        // Arrange — a lesson the tutor never marked completed or cancelled.
        IReadOnlyList<Lesson> schedule = [Entry(Now.AddMinutes(-1))];

        // Act
        var summary = ReportSummaryCalculator.Summarize(schedule, Now);

        // Assert
        Assert.Equal(1, summary.UnclosedCount);
        Assert.Equal(0, summary.UpcomingCount);
    }

    [Fact]
    public void Summarize_LessonStartingExactlyNow_CountsAsUpcoming()
    {
        // Arrange — the boundary is inclusive on the upcoming side.
        IReadOnlyList<Lesson> schedule = [Entry(Now)];

        // Act
        var summary = ReportSummaryCalculator.Summarize(schedule, Now);

        // Assert
        Assert.Equal(1, summary.UpcomingCount);
        Assert.Equal(0, summary.UnclosedCount);
    }

    [Fact]
    public void Summarize_CancelledLessons_ExcludedFromPlannedIncome()
    {
        // Arrange
        IReadOnlyList<Lesson> schedule =
        [
            Entry(Now.AddDays(1), price: 300m),
            Entry(Now.AddDays(2), LessonStatus.Cancelled, price: 500m),
        ];

        // Act
        var summary = ReportSummaryCalculator.Summarize(schedule, Now);

        // Assert — a cancelled lesson is owed nothing, so it never enters the money figures.
        Assert.Equal(300m, summary.PlannedIncome);
        Assert.Equal(0m, summary.ActualIncome);
    }

    [Fact]
    public void Summarize_PartiallyPaidSchedule_ComputesActualAndOutstanding()
    {
        // Arrange
        IReadOnlyList<Lesson> schedule =
        [
            Entry(Now.AddDays(-3), LessonStatus.Completed, price: 300m, isPaid: true),
            Entry(Now.AddDays(-2), LessonStatus.Completed, price: 250m, isPaid: true),
            Entry(Now.AddDays(-1), LessonStatus.Completed, price: 450m),
        ];

        // Act
        var summary = ReportSummaryCalculator.Summarize(schedule, Now);

        // Assert
        Assert.Equal(1000m, summary.PlannedIncome);
        Assert.Equal(550m, summary.ActualIncome);
    }

    [Fact]
    public void Summarize_FutureSeriesLessons_IncludedInPlannedIncome()
    {
        // Arrange — the lessons a series generated ahead are real future income and must be counted.
        var seriesId = Guid.NewGuid();
        IReadOnlyList<Lesson> schedule =
        [
            Entry(Now.AddDays(-1), LessonStatus.Completed, price: 200m, isPaid: true),
            Entry(Now.AddDays(1), price: 200m, seriesId: seriesId),
            Entry(Now.AddDays(8), price: 200m, seriesId: seriesId),
        ];

        // Act
        var summary = ReportSummaryCalculator.Summarize(schedule, Now);

        // Assert
        Assert.Equal(2, summary.UpcomingCount);
        Assert.Equal(600m, summary.PlannedIncome);
        Assert.Equal(200m, summary.ActualIncome);
    }
}
