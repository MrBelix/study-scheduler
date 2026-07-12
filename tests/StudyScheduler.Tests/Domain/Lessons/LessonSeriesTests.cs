using StudyScheduler.Domain.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Domain.Lessons;

public class LessonSeriesTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private static WeeklyPattern Pattern(Weekdays days = Weekdays.Monday | Weekdays.Thursday) =>
        WeeklyPattern.Create(days, new TimeOnly(16, 0), 60, London).Value;

    private static LessonSeries NewSeries(
        DateOnly? start = null,
        DateOnly? end = null,
        decimal? price = null,
        Weekdays days = Weekdays.Monday | Weekdays.Thursday) =>
        LessonSeries.Create(
            tutorTelegramId: 555,
            studentId: Guid.NewGuid(),
            pattern: Pattern(days),
            startDate: start ?? new DateOnly(2026, 7, 6), // a Monday
            createdAtUtc: CreatedAt,
            endDate: end,
            price: price).Value;

    [Fact]
    public void Create_Valid_SetsFields()
    {
        var series = NewSeries(price: 300m);

        Assert.NotEqual(Guid.Empty, series.Id);
        Assert.Equal(555, series.TutorTelegramId);
        Assert.Equal(Weekdays.Monday | Weekdays.Thursday, series.Pattern.Days);
        Assert.Equal(new TimeOnly(16, 0), series.Pattern.StartTimeLocal);
        Assert.Equal(300m, series.Price);
    }

    [Fact]
    public void Create_EndDateBeforeStart_Fails()
    {
        var result = LessonSeries.Create(
            555, Guid.NewGuid(), Pattern(), new DateOnly(2026, 7, 6), CreatedAt, endDate: new DateOnly(2026, 7, 5));

        Assert.False(result.IsSuccess);
        Assert.Equal("LessonSeries.EndDateBeforeStartDate", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Create_NegativePrice_Fails()
    {
        var result = LessonSeries.Create(
            555, Guid.NewGuid(), Pattern(), new DateOnly(2026, 7, 6), CreatedAt, price: -1m);

        Assert.False(result.IsSuccess);
        Assert.Equal("LessonSeries.NegativePrice", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void GetOccurrences_ClipsToStartAndEndDate()
    {
        var series = NewSeries(start: new DateOnly(2026, 7, 9), end: new DateOnly(2026, 7, 13));

        var dates = series
            .GetOccurrences(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31))
            .Select(o => o.OccurrenceDate);

        // Only Thu 09 and Mon 13 fall inside [09, 13] on Mon/Thu.
        Assert.Equal(new[] { new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 13) }, dates);
    }

    [Fact]
    public void End_MidLife_TightensEndDateOnly()
    {
        var series = NewSeries();
        series.End(new DateOnly(2026, 8, 1));
        Assert.Equal(new DateOnly(2026, 8, 1), series.EndDate);

        // Only ever tightened, never extended.
        series.End(new DateOnly(2026, 9, 1));
        Assert.Equal(new DateOnly(2026, 8, 1), series.EndDate);
    }

    [Fact]
    public void End_BeforeStart_ProducesNoOccurrences()
    {
        var series = NewSeries(start: new DateOnly(2026, 7, 6));
        series.End(new DateOnly(2026, 7, 1));

        Assert.Empty(series.GetOccurrences(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
    }

    [Fact]
    public void CancelAsOf_UsesPatternTimeZoneYesterday()
    {
        // Arrange
        var series = NewSeries(start: new DateOnly(2026, 1, 1));

        // Act
        // 2026-07-10 00:30 UTC is still 2026-07-10 01:30 in London (BST), so local "today" is Jul 10
        // and the last possible lesson day becomes the day before: Jul 09.
        series.CancelAsOf(new DateTimeOffset(2026, 7, 10, 0, 30, 0, TimeSpan.Zero));

        // Assert
        Assert.Equal(new DateOnly(2026, 7, 9), series.EndDate);
    }

    [Fact]
    public void CancelAsOf_ExistingEarlierEndDate_TightensEndDateOnly()
    {
        // Arrange
        var series = NewSeries(start: new DateOnly(2026, 1, 1), end: new DateOnly(2026, 6, 1));

        // Act
        // Local "today" is Jul 10 → cancel would set Jul 09, but the existing Jun 01 is earlier.
        series.CancelAsOf(new DateTimeOffset(2026, 7, 10, 0, 30, 0, TimeSpan.Zero));

        // Assert
        Assert.Equal(new DateOnly(2026, 6, 1), series.EndDate);
    }

    [Fact]
    public void CancelAsOf_SeriesStartingToday_ProducesNoOccurrences()
    {
        // Arrange
        // Local "today" is Jul 10; the series starts that same local day.
        var today = new DateOnly(2026, 7, 10);
        var series = NewSeries(start: today, days: Weekdays.Friday); // Jul 10 2026 is a Friday
        var now = new DateTimeOffset(2026, 7, 10, 0, 30, 0, TimeSpan.Zero);

        // Act
        series.CancelAsOf(now);

        // Assert
        Assert.Equal(new DateOnly(2026, 7, 9), series.EndDate); // yesterday < StartDate
        Assert.Empty(series.GetOccurrences(today, new DateOnly(2026, 12, 31)));
    }

    [Fact]
    public void UpdateDetails_EndDateBeforeStart_Fails()
    {
        var series = NewSeries(start: new DateOnly(2026, 7, 6));

        var result = series.UpdateDetails("New", new DateOnly(2026, 7, 5), null);

        Assert.False(result.IsSuccess);
        Assert.Equal("LessonSeries.EndDateBeforeStartDate", Assert.Single(result.Errors).Code);
    }
}
