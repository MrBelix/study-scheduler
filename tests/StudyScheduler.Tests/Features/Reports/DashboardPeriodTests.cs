using StudyScheduler.API.Features.Reports;
using Xunit;

namespace StudyScheduler.Tests.Features.Reports;

/// <summary>
/// Drives the dashboard's period arithmetic directly — parsing the query value, resolving the window
/// an anchor falls into, stepping one period back, projecting the window onto UTC and splitting it
/// into chart buckets. All pure; no database and no schedule involved.
/// </summary>
public class DashboardPeriodTests
{
    // 2026-07-08 is a Wednesday; its week runs Mon 2026-07-06 .. Sun 2026-07-12.
    private static readonly DateOnly Wednesday = new(2026, 7, 8);

    // Two hours ahead of UTC in July, and five behind — local midnight lands on either side of UTC's.
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("year")]
    [InlineData("weekly")]
    [InlineData("0")]
    public void ParseKind_UnknownValue_ReturnsNull(string? value)
    {
        // Arrange, Act
        var kind = DashboardPeriod.ParseKind(value);

        // Assert
        Assert.Null(kind);
    }

    [Theory]
    [InlineData("week", DashboardPeriodKind.Week)]
    [InlineData("Month", DashboardPeriodKind.Month)]
    [InlineData("QUARTER", DashboardPeriodKind.Quarter)]
    [InlineData(" week ", DashboardPeriodKind.Week)]
    public void ParseKind_KnownValueInAnyCasing_ReturnsKind(string value, DashboardPeriodKind expected)
    {
        // Arrange, Act
        var kind = DashboardPeriod.ParseKind(value);

        // Assert
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void Resolve_WeekAnchoredMidWeek_ReturnsMondayThroughSunday()
    {
        // Arrange, Act
        var period = DashboardPeriod.Resolve(DashboardPeriodKind.Week, Wednesday);

        // Assert
        Assert.Equal(new DateOnly(2026, 7, 6), period.From);
        Assert.Equal(new DateOnly(2026, 7, 12), period.To);
        Assert.Equal(DayOfWeek.Monday, period.From.DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, period.To.DayOfWeek);
    }

    [Fact]
    public void Resolve_WeekAnchoredOnSunday_ReturnsWeekStartingThePrecedingMonday()
    {
        // Arrange — Sunday is the last day of its week, not the first: the ISO convention.
        var sunday = new DateOnly(2026, 7, 12);

        // Act
        var period = DashboardPeriod.Resolve(DashboardPeriodKind.Week, sunday);

        // Assert
        Assert.Equal(new DateOnly(2026, 7, 6), period.From);
        Assert.Equal(sunday, period.To);
    }

    [Fact]
    public void Resolve_WeekAnchoredOnMonday_ReturnsThatSameWeek()
    {
        // Arrange
        var monday = new DateOnly(2026, 7, 6);

        // Act
        var period = DashboardPeriod.Resolve(DashboardPeriodKind.Week, monday);

        // Assert
        Assert.Equal(monday, period.From);
        Assert.Equal(new DateOnly(2026, 7, 12), period.To);
    }

    [Theory]
    [InlineData(2026, 7, 8, 2026, 7, 1, 2026, 7, 31)]
    [InlineData(2026, 7, 31, 2026, 7, 1, 2026, 7, 31)]
    [InlineData(2028, 2, 15, 2028, 2, 1, 2028, 2, 29)]   // leap February
    [InlineData(2026, 12, 1, 2026, 12, 1, 2026, 12, 31)]
    public void Resolve_Month_ReturnsFirstThroughLastDayOfTheAnchorsMonth(
        int year, int month, int day,
        int fromYear, int fromMonth, int fromDay,
        int toYear, int toMonth, int toDay)
    {
        // Arrange, Act
        var period = DashboardPeriod.Resolve(DashboardPeriodKind.Month, new DateOnly(year, month, day));

        // Assert
        Assert.Equal(new DateOnly(fromYear, fromMonth, fromDay), period.From);
        Assert.Equal(new DateOnly(toYear, toMonth, toDay), period.To);
    }

    [Theory]
    [InlineData(1, 1, 3, 31)]
    [InlineData(3, 1, 3, 31)]
    [InlineData(5, 4, 6, 30)]
    [InlineData(8, 7, 9, 30)]
    [InlineData(12, 10, 12, 31)]
    public void Resolve_Quarter_ReturnsTheCalendarQuarterContainingTheAnchor(
        int anchorMonth, int fromMonth, int toMonth, int toDay)
    {
        // Arrange, Act
        var period = DashboardPeriod.Resolve(DashboardPeriodKind.Quarter, new DateOnly(2026, anchorMonth, 15));

        // Assert
        Assert.Equal(new DateOnly(2026, fromMonth, 1), period.From);
        Assert.Equal(new DateOnly(2026, toMonth, toDay), period.To);
    }

    [Fact]
    public void Previous_Week_ReturnsThePrecedingMondayWeek()
    {
        // Arrange
        var period = DashboardPeriod.Resolve(DashboardPeriodKind.Week, Wednesday);

        // Act
        var previous = period.Previous;

        // Assert
        Assert.Equal(new DateOnly(2026, 6, 29), previous.From);
        Assert.Equal(new DateOnly(2026, 7, 5), previous.To);
    }

    [Fact]
    public void Previous_Month_ReturnsThePrecedingCalendarMonthWithItsOwnLength()
    {
        // Arrange — March back to a 29-day February: stepping by days would land mid-month.
        var period = DashboardPeriod.Resolve(DashboardPeriodKind.Month, new DateOnly(2028, 3, 20));

        // Act
        var previous = period.Previous;

        // Assert
        Assert.Equal(new DateOnly(2028, 2, 1), previous.From);
        Assert.Equal(new DateOnly(2028, 2, 29), previous.To);
    }

    [Fact]
    public void Previous_FirstQuarter_ReturnsTheLastQuarterOfThePrecedingYear()
    {
        // Arrange
        var period = DashboardPeriod.Resolve(DashboardPeriodKind.Quarter, new DateOnly(2026, 2, 10));

        // Act
        var previous = period.Previous;

        // Assert
        Assert.Equal(new DateOnly(2025, 10, 1), previous.From);
        Assert.Equal(new DateOnly(2025, 12, 31), previous.To);
    }

    [Fact]
    public void ToUtcWindow_ZoneAheadOfUtc_OpensBeforeMidnightUtcOnTheDayBefore()
    {
        // Arrange — Berlin is UTC+2 in July, so its local midnight is 22:00 UTC the previous day.
        var period = DashboardPeriod.Resolve(DashboardPeriodKind.Week, Wednesday);

        // Act
        var (fromUtc, toUtc) = period.ToUtcWindow(Berlin);

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 7, 5, 22, 0, 0, TimeSpan.Zero), fromUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 12, 22, 0, 0, TimeSpan.Zero), toUtc);
    }

    [Fact]
    public void ToUtcWindow_ZoneBehindUtc_OpensAfterMidnightUtcOnTheSameDay()
    {
        // Arrange — New York is UTC-4 in July, so its local midnight is 04:00 UTC the same day.
        var period = DashboardPeriod.Resolve(DashboardPeriodKind.Week, Wednesday);

        // Act
        var (fromUtc, toUtc) = period.ToUtcWindow(NewYork);

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 7, 6, 4, 0, 0, TimeSpan.Zero), fromUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 13, 4, 0, 0, TimeSpan.Zero), toUtc);
    }

    [Fact]
    public void ToUtcWindow_MonthEndingAcrossADstTransition_TracksTheZonesOffsetOnEachBound()
    {
        // Arrange — Europe/Berlin leaves DST on 2026-10-25: October opens at UTC+2 and closes at UTC+1.
        var period = DashboardPeriod.Resolve(DashboardPeriodKind.Month, new DateOnly(2026, 10, 15));

        // Act
        var (fromUtc, toUtc) = period.ToUtcWindow(Berlin);

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 22, 0, 0, TimeSpan.Zero), fromUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 23, 0, 0, TimeSpan.Zero), toUtc);
    }

    [Fact]
    public void SplitIntoBuckets_Week_ReturnsOneSingleDayBucketPerDay()
    {
        // Arrange
        var period = DashboardPeriod.Resolve(DashboardPeriodKind.Week, Wednesday);

        // Act
        var buckets = period.SplitIntoBuckets();

        // Assert
        Assert.Equal(7, buckets.Count);
        Assert.All(buckets, b => Assert.Equal(b.From, b.To));
        Assert.Equal(new DateOnly(2026, 7, 6), buckets[0].From);
        Assert.Equal(new DateOnly(2026, 7, 12), buckets[6].To);
    }

    [Fact]
    public void SplitIntoBuckets_Month_ClipsTheFirstAndLastWeekToThePeriod()
    {
        // Arrange — July 2026 opens on a Wednesday and closes on a Friday.
        var period = DashboardPeriod.Resolve(DashboardPeriodKind.Month, Wednesday);

        // Act
        var buckets = period.SplitIntoBuckets();

        // Assert
        Assert.Equal(5, buckets.Count);
        Assert.Equal((new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 5)), buckets[0]);
        Assert.Equal((new DateOnly(2026, 7, 6), new DateOnly(2026, 7, 12)), buckets[1]);
        Assert.Equal((new DateOnly(2026, 7, 27), new DateOnly(2026, 7, 31)), buckets[4]);
    }

    [Fact]
    public void SplitIntoBuckets_Month_MiddleBucketsAreFullMondayToSundayWeeks()
    {
        // Arrange
        var period = DashboardPeriod.Resolve(DashboardPeriodKind.Month, Wednesday);

        // Act
        var buckets = period.SplitIntoBuckets();

        // Assert
        Assert.All(buckets.Skip(1).SkipLast(1), b =>
        {
            Assert.Equal(DayOfWeek.Monday, b.From.DayOfWeek);
            Assert.Equal(DayOfWeek.Sunday, b.To.DayOfWeek);
        });
    }

    [Fact]
    public void SplitIntoBuckets_Quarter_CoversEveryDayOfThePeriodExactlyOnce()
    {
        // Arrange — Q3 2026 runs 2026-07-01 .. 2026-09-30, 92 days.
        var period = DashboardPeriod.Resolve(DashboardPeriodKind.Quarter, new DateOnly(2026, 8, 20));

        // Act
        var buckets = period.SplitIntoBuckets();

        // Assert
        Assert.Equal(period.From, buckets[0].From);
        Assert.Equal(period.To, buckets[^1].To);
        Assert.Equal(92, buckets.Sum(b => b.To.DayNumber - b.From.DayNumber + 1));
        for (var i = 1; i < buckets.Count; i++)
            Assert.Equal(buckets[i - 1].To.AddDays(1), buckets[i].From);
    }

    [Fact]
    public void DayCount_Month_CountsBothBoundsInclusive()
    {
        // Arrange
        var period = DashboardPeriod.Resolve(DashboardPeriodKind.Month, Wednesday);

        // Act
        var days = period.DayCount;

        // Assert
        Assert.Equal(31, days);
    }
}
