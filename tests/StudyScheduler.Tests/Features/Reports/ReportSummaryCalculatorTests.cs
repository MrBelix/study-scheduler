using StudyScheduler.API.Core.Scheduling;
using StudyScheduler.API.Features.Reports;
using StudyScheduler.Domain.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Features.Reports;

/// <summary>
/// Drives the pure aggregation seam directly — <see cref="ScheduleReader"/> is sealed and needs a
/// database, so the arithmetic is exercised on hand-built <see cref="ScheduleEntry"/> lists.
/// </summary>
public class ReportSummaryCalculatorTests
{
    private static readonly Guid Student = Guid.NewGuid();
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private static ScheduleEntry Entry(
        DateTimeOffset startUtc,
        LessonStatus status = LessonStatus.Scheduled,
        decimal price = 100m,
        bool isPaid = false,
        bool isVirtual = false) =>
        new(
            isVirtual ? null : Guid.NewGuid(),
            Student,
            isVirtual ? Guid.NewGuid() : null,
            isVirtual ? DateOnly.FromDateTime(startUtc.UtcDateTime) : null,
            startUtc,
            startUtc.AddMinutes(60),
            60,
            status,
            price,
            isPaid,
            Topic: null,
            Description: null,
            IsVirtual: isVirtual,
            CreatedAtUtc: CreatedAt,
            Notifications: NotificationState.None);

    [Fact]
    public void Summarize_EmptySchedule_ReturnsZeros()
    {
        // Arrange
        IReadOnlyList<ScheduleEntry> entries = [];

        // Act
        var summary = ReportSummaryCalculator.Summarize(entries, Now);

        // Assert
        Assert.Equal(0, summary.TotalCount);
        Assert.Equal(0, summary.CompletedCount);
        Assert.Equal(0, summary.CancelledCount);
        Assert.Equal(0, summary.UpcomingCount);
        Assert.Equal(0, summary.UnclosedCount);
        Assert.Equal(0, summary.PaidCount);
        Assert.Equal(0m, summary.PlannedIncome);
        Assert.Equal(0m, summary.ActualIncome);
        Assert.Equal(0m, summary.OutstandingIncome);
    }

    [Fact]
    public void Summarize_MixedStatuses_CountsEachBucket()
    {
        // Arrange
        IReadOnlyList<ScheduleEntry> entries =
        [
            Entry(Now.AddDays(-2), LessonStatus.Completed, isPaid: true),
            Entry(Now.AddDays(-1), LessonStatus.Cancelled),
            Entry(Now.AddDays(-1), LessonStatus.Scheduled),   // past, still open
            Entry(Now.AddDays(1), LessonStatus.Scheduled),    // upcoming
            Entry(Now.AddDays(2), LessonStatus.Scheduled),    // upcoming
        ];

        // Act
        var summary = ReportSummaryCalculator.Summarize(entries, Now);

        // Assert
        Assert.Equal(5, summary.TotalCount);
        Assert.Equal(1, summary.CompletedCount);
        Assert.Equal(1, summary.CancelledCount);
        Assert.Equal(2, summary.UpcomingCount);
        Assert.Equal(1, summary.UnclosedCount);
        Assert.Equal(1, summary.PaidCount);
    }

    [Fact]
    public void Summarize_PastScheduledEntry_CountsAsUnclosed()
    {
        // Arrange — a lesson the tutor never marked completed or cancelled.
        IReadOnlyList<ScheduleEntry> entries = [Entry(Now.AddMinutes(-1))];

        // Act
        var summary = ReportSummaryCalculator.Summarize(entries, Now);

        // Assert
        Assert.Equal(1, summary.UnclosedCount);
        Assert.Equal(0, summary.UpcomingCount);
    }

    [Fact]
    public void Summarize_EntryStartingExactlyNow_CountsAsUpcoming()
    {
        // Arrange — the boundary is inclusive on the upcoming side.
        IReadOnlyList<ScheduleEntry> entries = [Entry(Now)];

        // Act
        var summary = ReportSummaryCalculator.Summarize(entries, Now);

        // Assert
        Assert.Equal(1, summary.UpcomingCount);
        Assert.Equal(0, summary.UnclosedCount);
    }

    [Fact]
    public void Summarize_CancelledEntries_ExcludedFromPlannedIncome()
    {
        // Arrange
        IReadOnlyList<ScheduleEntry> entries =
        [
            Entry(Now.AddDays(1), price: 300m),
            Entry(Now.AddDays(2), LessonStatus.Cancelled, price: 500m),
        ];

        // Act
        var summary = ReportSummaryCalculator.Summarize(entries, Now);

        // Assert — a cancelled lesson is owed nothing, so it never enters the money figures.
        Assert.Equal(300m, summary.PlannedIncome);
        Assert.Equal(0m, summary.ActualIncome);
        Assert.Equal(300m, summary.OutstandingIncome);
    }

    [Fact]
    public void Summarize_PartiallyPaidSchedule_ComputesActualAndOutstanding()
    {
        // Arrange
        IReadOnlyList<ScheduleEntry> entries =
        [
            Entry(Now.AddDays(-3), LessonStatus.Completed, price: 300m, isPaid: true),
            Entry(Now.AddDays(-2), LessonStatus.Completed, price: 250m, isPaid: true),
            Entry(Now.AddDays(-1), LessonStatus.Completed, price: 450m),
        ];

        // Act
        var summary = ReportSummaryCalculator.Summarize(entries, Now);

        // Assert
        Assert.Equal(1000m, summary.PlannedIncome);
        Assert.Equal(550m, summary.ActualIncome);
        Assert.Equal(450m, summary.OutstandingIncome);
        Assert.Equal(2, summary.PaidCount);
    }

    [Fact]
    public void Summarize_VirtualOccurrences_IncludedInPlannedIncome()
    {
        // Arrange — unmaterialized series slots are real future income and must be counted.
        IReadOnlyList<ScheduleEntry> entries =
        [
            Entry(Now.AddDays(-1), LessonStatus.Completed, price: 200m, isPaid: true),
            Entry(Now.AddDays(1), price: 200m, isVirtual: true),
            Entry(Now.AddDays(8), price: 200m, isVirtual: true),
        ];

        // Act
        var summary = ReportSummaryCalculator.Summarize(entries, Now);

        // Assert
        Assert.Equal(3, summary.TotalCount);
        Assert.Equal(2, summary.UpcomingCount);
        Assert.Equal(600m, summary.PlannedIncome);
        Assert.Equal(200m, summary.ActualIncome);
        Assert.Equal(400m, summary.OutstandingIncome);
    }
}
