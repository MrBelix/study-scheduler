using StudyScheduler.Domain.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Domain.Lessons;

public class LessonSeriesTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid StudentId = Guid.NewGuid();
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

    // Monday.
    private static readonly DateOnly StartDate = new(2026, 7, 6);
    private static readonly TimeOnly FourPm = new(16, 0);

    private static LessonSeries CreateSeries(
        Weekdays weekdays = Weekdays.Monday,
        DateOnly? endDate = null,
        TimeZoneInfo? timeZone = null,
        TimeOnly? startTime = null,
        DateOnly? startDate = null) =>
        LessonSeries.Create(
            555,
            StudentId,
            startDate ?? StartDate,
            weekdays,
            startTime ?? FourPm,
            60,
            timeZone ?? Kyiv,
            CreatedAt,
            title: "Math",
            endDate: endDate);

    [Fact]
    public void Create_ValidInput_SetsFieldsAndIsActive()
    {
        var series = CreateSeries(weekdays: Weekdays.Monday | Weekdays.Thursday, endDate: new DateOnly(2026, 12, 28));

        Assert.NotEqual(Guid.Empty, series.Id);
        Assert.Equal(555, series.TutorTelegramId);
        Assert.Equal(StudentId, series.StudentId);
        Assert.Equal("Math", series.Title);
        Assert.Equal(StartDate, series.StartDate);
        Assert.Equal(new DateOnly(2026, 12, 28), series.EndDate);
        Assert.Equal(Weekdays.Monday | Weekdays.Thursday, series.Weekdays);
        Assert.Equal(FourPm, series.StartTimeLocal);
        Assert.Equal(60, series.DurationMinutes);
        Assert.Equal("Europe/Kyiv", series.TimeZone.Id);
        Assert.Null(series.Price);
        Assert.True(series.IsActive);
    }

    [Fact]
    public void Create_EndDateBeforeStartDate_Throws()
    {
        Assert.Throws<ArgumentException>(() => CreateSeries(endDate: StartDate.AddDays(-1)));
    }

    [Fact]
    public void Create_NullTimeZone_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LessonSeries.Create(
            555, StudentId, StartDate, Weekdays.Monday, FourPm, 60, null!, CreatedAt));
    }

    [Theory]
    [InlineData(Weekdays.None)]
    [InlineData((Weekdays)(1 << 7))] // a bit outside All
    public void Create_InvalidWeekdays_Throws(Weekdays weekdays)
    {
        Assert.Throws<ArgumentException>(() => CreateSeries(weekdays: weekdays));
    }

    [Fact]
    public void Create_NegativePrice_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LessonSeries.Create(
            555, StudentId, StartDate, Weekdays.Monday, FourPm, 60, Kyiv, CreatedAt, price: -1m));
    }

    [Fact]
    public void GetOccurrences_SingleWeekday_SevenDaysApart()
    {
        var series = CreateSeries();

        var occurrences = series.GetOccurrences(StartDate, StartDate.AddDays(27));

        Assert.Equal(4, occurrences.Count);
        Assert.Equal(
            [StartDate, StartDate.AddDays(7), StartDate.AddDays(14), StartDate.AddDays(21)],
            occurrences.Select(o => o.OccurrenceDate));
        // Kyiv is UTC+3 in July: 16:00 local == 13:00 UTC.
        Assert.All(occurrences, o =>
        {
            Assert.Equal(13, o.StartUtc.Hour);
            Assert.Equal(TimeSpan.Zero, o.StartUtc.Offset);
            Assert.Equal(o.StartUtc.AddMinutes(60), o.EndUtc);
        });
    }

    [Fact]
    public void GetOccurrences_MultipleWeekdays_AllMatchingDates()
    {
        var series = CreateSeries(weekdays: Weekdays.Monday | Weekdays.Thursday);

        var occurrences = series.GetOccurrences(StartDate, StartDate.AddDays(13));

        // Two weeks of Mon + Thu.
        Assert.Equal(
            [StartDate, StartDate.AddDays(3), StartDate.AddDays(7), StartDate.AddDays(10)],
            occurrences.Select(o => o.OccurrenceDate));
    }

    [Fact]
    public void GetOccurrences_StartDateIsNotALessonDay_FirstHitIsNextMatchingWeekday()
    {
        // Schedule takes effect on Saturday; lessons run on Mondays.
        var series = CreateSeries(startDate: StartDate.AddDays(-2));

        var occurrences = series.GetOccurrences(StartDate.AddDays(-2), StartDate.AddDays(6));

        var single = Assert.Single(occurrences);
        Assert.Equal(StartDate, single.OccurrenceDate);
    }

    [Fact]
    public void GetOccurrences_RangeStartsMidSeries_SkipsEarlierDates()
    {
        var series = CreateSeries();

        // Range starts on a Wednesday between occurrences: first hit is the next Monday.
        var occurrences = series.GetOccurrences(StartDate.AddDays(2), StartDate.AddDays(16));

        Assert.Equal(
            [StartDate.AddDays(7), StartDate.AddDays(14)],
            occurrences.Select(o => o.OccurrenceDate));
    }

    [Fact]
    public void GetOccurrences_ClippedByEndDate()
    {
        var series = CreateSeries(endDate: StartDate.AddDays(7));

        var occurrences = series.GetOccurrences(StartDate, StartDate.AddDays(100));

        Assert.Equal(2, occurrences.Count);
    }

    [Fact]
    public void GetOccurrences_RangeBeforeStartDate_Empty()
    {
        var series = CreateSeries();

        var occurrences = series.GetOccurrences(StartDate.AddDays(-30), StartDate.AddDays(-1));

        Assert.Empty(occurrences);
    }

    [Fact]
    public void GetOccurrences_OpenEndedSeries_FillsWholeRequestedRange()
    {
        var series = CreateSeries(endDate: null);

        var occurrences = series.GetOccurrences(StartDate.AddDays(700), StartDate.AddDays(727));

        Assert.Equal(4, occurrences.Count);
    }

    [Fact]
    public void GetOccurrences_AcrossAutumnDstTransition_KeepsLocalTime()
    {
        // Kyiv leaves DST on 2026-10-25: UTC offset changes +3 -> +2.
        var series = CreateSeries(startDate: new DateOnly(2026, 10, 19)); // Monday before transition

        var occurrences = series.GetOccurrences(new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 26));

        Assert.Equal(2, occurrences.Count);
        Assert.Equal(13, occurrences[0].StartUtc.Hour); // 16:00 +03:00
        Assert.Equal(14, occurrences[1].StartUtc.Hour); // 16:00 +02:00 — same local wall clock
    }

    [Fact]
    public void GetOccurrences_AcrossSpringDstTransition_KeepsLocalTime()
    {
        // Kyiv enters DST on 2026-03-29: UTC offset changes +2 -> +3.
        var series = CreateSeries(startDate: new DateOnly(2026, 3, 23)); // Monday before transition

        var occurrences = series.GetOccurrences(new DateOnly(2026, 3, 23), new DateOnly(2026, 3, 30));

        Assert.Equal(14, occurrences[0].StartUtc.Hour); // 16:00 +02:00
        Assert.Equal(13, occurrences[1].StartUtc.Hour); // 16:00 +03:00 — same local wall clock
    }

    [Fact]
    public void GetOccurrences_StartInSpringForwardGap_ShiftsOneHourLater()
    {
        // 2026-03-29 03:30 does not exist in Kyiv (clocks jump 03:00 -> 04:00).
        var series = CreateSeries(
            weekdays: Weekdays.Sunday,
            startDate: new DateOnly(2026, 3, 29), // Sunday of the transition
            startTime: new TimeOnly(3, 30));

        var occurrences = series.GetOccurrences(new DateOnly(2026, 3, 29), new DateOnly(2026, 3, 29));

        var single = Assert.Single(occurrences);
        // Shifted to 04:30 +03:00 == 01:30 UTC.
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), single.StartUtc);
    }

    [Fact]
    public void UpdateDetails_ReplacesEditableFields()
    {
        var series = CreateSeries();

        series.UpdateDetails("  Physics  ", StartDate.AddDays(14), 300m);

        Assert.Equal("Physics", series.Title);
        Assert.Equal(StartDate.AddDays(14), series.EndDate);
        Assert.Equal(300m, series.Price);
    }

    [Fact]
    public void UpdateDetails_EndDateBeforeStartDate_Throws()
    {
        var series = CreateSeries();

        Assert.Throws<ArgumentException>(() => series.UpdateDetails(null, StartDate.AddDays(-7), null));
    }

    [Fact]
    public void Deactivate_TurnsSeriesInactive()
    {
        var series = CreateSeries();

        series.Deactivate();

        Assert.False(series.IsActive);
    }
}
