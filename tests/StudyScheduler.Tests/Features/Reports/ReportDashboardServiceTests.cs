using StudyScheduler.API.Features.Reports;
using StudyScheduler.Domain.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Features.Reports;

/// <summary>
/// Drives the dashboard's pure aggregation seam directly — the service's own queries need a
/// database, so the arithmetic runs on hand-built <see cref="Lesson"/> lists.
/// The end-to-end wiring (profile, schedule, debt ledger, names) is covered by
/// <see cref="DashboardEndpointTests"/>.
/// </summary>
public class ReportDashboardServiceTests
{
    private const decimal PriceUnit = 100m;

    private static readonly Guid Ann = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid Cid = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<Guid, string> Names =
        new Dictionary<Guid, string> { [Ann] = "Ann", [Bob] = "Bob", [Cid] = "Cid" };

    // Berlin is UTC+2 in July, so a local day starts at 22:00 UTC the day before.
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static readonly DateTimeOffset CreatedAt = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // Wednesday of the week 2026-07-06 .. 2026-07-12, mid-morning.
    private static readonly DateTimeOffset Now = new(2026, 7, 8, 9, 0, 0, TimeSpan.Zero);

    private static readonly DashboardPeriod Week =
        DashboardPeriod.Resolve(DashboardPeriodKind.Week, new DateOnly(2026, 7, 8));

    private static readonly DashboardPeriod Month =
        DashboardPeriod.Resolve(DashboardPeriodKind.Month, new DateOnly(2026, 7, 8));

    [Fact]
    public void Build_MixedSchedule_IncomeMirrorsTheSummaryCalculator()
    {
        // Arrange — the same entries the summary calculator would be given.
        IReadOnlyList<Lesson> entries =
        [
            Entry(Local(7, 6, 10), LessonStatus.Completed, price: 300m, isPaid: true),
            Entry(Local(7, 7, 10), LessonStatus.Completed, price: 200m),
            Entry(Local(7, 9, 10), price: 150m),
            Entry(Local(7, 10, 10), LessonStatus.Cancelled, price: 500m),
        ];
        var summary = ReportSummaryCalculator.Summarize(entries, Now);

        // Act
        var dashboard = Build(Week, entries);

        // Assert — parity is the point: the dashboard reuses the summary, it does not recompute.
        Assert.Equal(summary.ActualIncome, dashboard.Income.Actual);
        Assert.Equal(summary.PlannedIncome, dashboard.Income.Expected);
        Assert.Equal(300m, dashboard.Income.Actual);
        Assert.Equal(650m, dashboard.Income.Expected);
    }

    [Fact]
    public void Build_CancelledEntries_ExcludedFromBothIncomeFigures()
    {
        // Arrange
        IReadOnlyList<Lesson> entries =
        [
            Entry(Local(7, 6, 10), price: 300m),
            Entry(Local(7, 7, 10), LessonStatus.Cancelled, price: 500m),
        ];

        // Act
        var dashboard = Build(Week, entries);

        // Assert — a cancelled lesson is owed nothing, so it never enters the money figures.
        Assert.Equal(0m, dashboard.Income.Actual);
        Assert.Equal(300m, dashboard.Income.Expected);
    }

    [Fact]
    public void Build_PreviousPeriodSchedule_ReportedAsItsReceivedIncome()
    {
        // Arrange — the baseline period carries money of its own; only what was paid counts.
        IReadOnlyList<Lesson> previous =
        [
            Entry(Local(6, 29, 10), LessonStatus.Completed, price: 400m, isPaid: true),
            Entry(Local(6, 30, 10), LessonStatus.Completed, price: 250m),
        ];

        // Act
        var dashboard = Build(Week, [Entry(Local(7, 6, 10), LessonStatus.Completed, isPaid: true)], previous);

        // Assert
        Assert.Equal(400m, dashboard.Income.Previous);
        Assert.Equal(PriceUnit, dashboard.Income.Actual);
    }

    [Fact]
    public void Build_MixedStatuses_CountsCompletedScheduledAndCancelled()
    {
        // Arrange — one scheduled entry is already in the past, one is still ahead.
        IReadOnlyList<Lesson> entries =
        [
            Entry(Local(7, 6, 10), LessonStatus.Completed),
            Entry(Local(7, 7, 10), LessonStatus.Completed),
            Entry(Local(7, 7, 12)),
            Entry(Local(7, 9, 10)),
            Entry(Local(7, 10, 10), LessonStatus.Cancelled),
        ];

        // Act
        var dashboard = Build(Week, entries);

        // Assert — the dashboard does not split scheduled by whether it already happened.
        Assert.Equal(2, dashboard.Lessons.Completed);
        Assert.Equal(2, dashboard.Lessons.Scheduled);
        Assert.Equal(1, dashboard.Lessons.Cancelled);
    }

    [Fact]
    public void Build_WeekPeriod_AveragesNonCancelledHoursOverExactlyOneWeek()
    {
        // Arrange — four 90-minute lessons is six hours; a cancelled one is no load at all.
        IReadOnlyList<Lesson> entries =
        [
            Entry(Local(7, 6, 10), durationMinutes: 90),
            Entry(Local(7, 7, 10), durationMinutes: 90),
            Entry(Local(7, 8, 10), durationMinutes: 90),
            Entry(Local(7, 9, 10), LessonStatus.Completed, durationMinutes: 90),
            Entry(Local(7, 10, 10), LessonStatus.Cancelled, durationMinutes: 600),
        ];

        // Act
        var dashboard = Build(Week, entries);

        // Assert
        Assert.Equal(6.0m, dashboard.WeeklyLoad.Hours);
        Assert.Equal(4, dashboard.WeeklyLoad.LessonsInPeriod);
    }

    [Fact]
    public void Build_MonthPeriod_AveragesHoursOverTheWeeksThePeriodActuallySpans()
    {
        // Arrange — ten one-hour lessons across a 31-day month, i.e. 31/7 weeks.
        IReadOnlyList<Lesson> entries =
            [.. Enumerable.Range(1, 10).Select(day => Entry(Local(7, day, 10)))];

        // Act
        var dashboard = Build(Month, entries);

        // Assert — 10h / (31/7 weeks) = 2.258…, rounded to one decimal.
        Assert.Equal(2.3m, dashboard.WeeklyLoad.Hours);
        Assert.Equal(10, dashboard.WeeklyLoad.LessonsInPeriod);
    }

    [Fact]
    public void Build_WeekPeriod_ProducesOneBucketPerDay()
    {
        // Arrange
        IReadOnlyList<Lesson> entries =
        [
            Entry(Local(7, 6, 10), LessonStatus.Completed),
            Entry(Local(7, 6, 12), LessonStatus.Completed),
            Entry(Local(7, 9, 10)),
        ];

        // Act
        var dashboard = Build(Week, entries);

        // Assert
        Assert.Equal(7, dashboard.Buckets.Count);
        Assert.All(dashboard.Buckets, b => Assert.Equal(b.From, b.To));
        Assert.Equal(2, dashboard.Buckets[0].CompletedCount);
        Assert.Equal(0, dashboard.Buckets[0].ScheduledCount);
        Assert.Equal(1, dashboard.Buckets[3].ScheduledCount);
    }

    [Fact]
    public void Build_MonthPeriod_ProducesWeekBucketsClippedToThePeriod()
    {
        // Arrange — July 2026 opens on a Wednesday and closes on a Friday.
        IReadOnlyList<Lesson> entries =
        [
            Entry(Local(7, 1, 10), LessonStatus.Completed),
            Entry(Local(7, 8, 10), LessonStatus.Completed),
            Entry(Local(7, 31, 10)),
        ];

        // Act
        var dashboard = Build(Month, entries);

        // Assert
        Assert.Equal(5, dashboard.Buckets.Count);
        Assert.Equal(new DateOnly(2026, 7, 1), dashboard.Buckets[0].From);
        Assert.Equal(new DateOnly(2026, 7, 5), dashboard.Buckets[0].To);
        Assert.Equal(1, dashboard.Buckets[0].CompletedCount);
        Assert.Equal(1, dashboard.Buckets[1].CompletedCount);
        Assert.Equal(new DateOnly(2026, 7, 27), dashboard.Buckets[4].From);
        Assert.Equal(new DateOnly(2026, 7, 31), dashboard.Buckets[4].To);
        Assert.Equal(1, dashboard.Buckets[4].ScheduledCount);
    }

    [Fact]
    public void Build_MonthPeriod_BucketCountsSumToThePeriodsLessonCounts()
    {
        // Arrange — one entry per day across the whole month, cycling through the statuses.
        IReadOnlyList<Lesson> entries =
        [
            .. Enumerable.Range(1, 31).Select(day => Entry(
                Local(7, day, 10),
                (LessonStatus)(day % 3))),
        ];

        // Act
        var dashboard = Build(Month, entries);

        // Assert — nothing is dropped or double-counted by the bucketing.
        Assert.Equal(dashboard.Lessons.Completed, dashboard.Buckets.Sum(b => b.CompletedCount));
        Assert.Equal(dashboard.Lessons.Scheduled, dashboard.Buckets.Sum(b => b.ScheduledCount));
    }

    [Fact]
    public void Build_EntryStartingBeforeThePeriodInLocalTime_CountedInTheFirstBucket()
    {
        // Arrange — 21:30 UTC is 23:30 Berlin on Sunday 5 July: the lesson runs into Monday, so the
        // schedule window pulls it in even though its local start date is outside the period.
        IReadOnlyList<Lesson> entries =
            [Entry(new DateTimeOffset(2026, 7, 5, 21, 30, 0, TimeSpan.Zero), LessonStatus.Completed)];

        // Act
        var dashboard = Build(Week, entries);

        // Assert
        Assert.Equal(1, dashboard.Lessons.Completed);
        Assert.Equal(1, dashboard.Buckets[0].CompletedCount);
        Assert.Equal(1, dashboard.Buckets.Sum(b => b.CompletedCount));
    }

    [Fact]
    public void Build_EmptySchedule_StillReturnsTheFullBucketAxis()
    {
        // Arrange
        IReadOnlyList<Lesson> entries = [];

        // Act
        var dashboard = Build(Week, entries);

        // Assert — the chart keeps its shape so the client never has to fill gaps.
        Assert.Equal(7, dashboard.Buckets.Count);
        Assert.All(dashboard.Buckets, b => Assert.Equal(0, b.CompletedCount + b.ScheduledCount));
        Assert.Equal(0m, dashboard.Income.Actual);
        Assert.Equal(0m, dashboard.WeeklyLoad.Hours);
        Assert.Empty(dashboard.PerStudent);
    }

    [Fact]
    public void Build_PerStudent_OrdersByIncomeDescendingAndExcludesStudentsWhoPaidNothing()
    {
        // Arrange
        IReadOnlyList<Lesson> entries =
        [
            Entry(Local(7, 6, 10), LessonStatus.Completed, price: 200m, isPaid: true, studentId: Ann),
            Entry(Local(7, 7, 10), LessonStatus.Completed, price: 500m, isPaid: true, studentId: Bob),
            Entry(Local(7, 8, 10), LessonStatus.Completed, price: 400m, studentId: Cid),
        ];

        // Act
        var dashboard = Build(Week, entries);

        // Assert — Cid owes 400 but has paid nothing, so the "who brought money in" list drops them.
        Assert.Equal(["Bob", "Ann"], dashboard.PerStudent.Select(s => s.Name));
        Assert.Equal([500m, 200m], dashboard.PerStudent.Select(s => s.Income));
        Assert.Equal(Bob, dashboard.PerStudent[0].StudentId);
    }

    [Fact]
    public void Build_PerStudentIncome_SumsToThePeriodsActualIncome()
    {
        // Arrange
        IReadOnlyList<Lesson> entries =
        [
            Entry(Local(7, 6, 10), LessonStatus.Completed, price: 200m, isPaid: true, studentId: Ann),
            Entry(Local(7, 6, 12), LessonStatus.Completed, price: 150m, isPaid: true, studentId: Ann),
            Entry(Local(7, 7, 10), LessonStatus.Completed, price: 500m, isPaid: true, studentId: Bob),
            Entry(Local(7, 8, 10), LessonStatus.Cancelled, price: 900m, studentId: Bob),
        ];

        // Act
        var dashboard = Build(Week, entries);

        // Assert
        Assert.Equal(dashboard.Income.Actual, dashboard.PerStudent.Sum(s => s.Income));
        Assert.Equal(850m, dashboard.Income.Actual);
    }

    [Fact]
    public void Build_Debtors_OrderedByAmountDescendingWithNamesEmbedded()
    {
        // Arrange
        IReadOnlyList<StudentDebt> debts =
        [
            new(Ann, 300m, 3, new DateTimeOffset(2026, 3, 2, 10, 0, 0, TimeSpan.Zero)),
            new(Bob, 750m, 5, new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero)),
        ];

        // Act
        var dashboard = Build(Week, [], debts: debts);

        // Assert
        Assert.Equal(["Bob", "Ann"], dashboard.Debt.Debtors.Select(d => d.Name));
        Assert.Equal(750m, dashboard.Debt.Debtors[0].Amount);
        Assert.Equal(5, dashboard.Debt.Debtors[0].LessonsCount);
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero), dashboard.Debt.Debtors[0].OldestUtc);
        Assert.Equal(1050m, dashboard.Debt.Total);
    }

    [Fact]
    public void Build_DebtorForAStudentMissingFromTheLookup_FallsBackToAnEmptyName()
    {
        // Arrange — a data anomaly must not take the whole dashboard down, nor hide the money.
        IReadOnlyList<StudentDebt> debts =
            [new(Guid.NewGuid(), 120m, 1, new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero))];

        // Act
        var dashboard = Build(Week, [], debts: debts);

        // Assert
        Assert.Equal(string.Empty, Assert.Single(dashboard.Debt.Debtors).Name);
        Assert.Equal(120m, dashboard.Debt.Total);
    }

    [Fact]
    public void Build_AnyPeriod_EchoesItsLocalDateBounds()
    {
        // Arrange, Act
        var dashboard = Build(Month, []);

        // Assert
        Assert.Equal(new DateOnly(2026, 7, 1), dashboard.Period.From);
        Assert.Equal(new DateOnly(2026, 7, 31), dashboard.Period.To);
    }

    private static DashboardResponse Build(
        DashboardPeriod period,
        IReadOnlyList<Lesson> schedule,
        IReadOnlyList<Lesson>? previousSchedule = null,
        IReadOnlyList<StudentDebt>? debts = null) =>
        ReportDashboardService.Build(period, schedule, previousSchedule ?? [], debts ?? [], Names, Berlin, Now);

    /// <summary>The UTC instant of a July 2026 wall clock in Berlin (UTC+2).</summary>
    private static DateTimeOffset Local(int month, int day, int hourLocal) =>
        new(2026, month, day, hourLocal - 2, 0, 0, TimeSpan.Zero);

    private static Lesson Entry(
        DateTimeOffset startUtc,
        LessonStatus status = LessonStatus.Scheduled,
        decimal price = PriceUnit,
        bool isPaid = false,
        int durationMinutes = 60,
        Guid? studentId = null)
    {
        var lesson = Lesson.Create(
            studentId ?? Ann, startUtc, durationMinutes, price, CreatedAt).Value;
        if (status != LessonStatus.Scheduled)
            lesson.ChangeStatus(status);
        // After the status: cancelling clears the payment flag, so the order is not interchangeable.
        lesson.SetPaid(isPaid);
        return lesson;
    }
}
