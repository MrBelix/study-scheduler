using StudyScheduler.Domain.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Domain.Lessons;

public class WeeklyPatternTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private static WeeklyPattern Pattern(Weekdays days = Weekdays.Monday | Weekdays.Thursday, int duration = 60) =>
        WeeklyPattern.Create(days, new TimeOnly(16, 0), duration, London).Value;

    [Fact]
    public void Create_Valid_SetsFields()
    {
        var pattern = Pattern();

        Assert.Equal(Weekdays.Monday | Weekdays.Thursday, pattern.Days);
        Assert.Equal(new TimeOnly(16, 0), pattern.StartTimeLocal);
        Assert.Equal(60, pattern.DurationMinutes);
        Assert.Equal(London, pattern.TimeZone);
    }

    [Fact]
    public void Create_InvalidWeekdays_Fails()
    {
        var result = WeeklyPattern.Create(Weekdays.None, new TimeOnly(16, 0), 60, London);

        Assert.False(result.IsSuccess);
        Assert.Equal("WeeklyPattern.InvalidWeekdays", Assert.Single(result.Errors).Code);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(601)]
    public void Create_DurationOutOfRange_Fails(int duration)
    {
        var result = WeeklyPattern.Create(Weekdays.Monday, new TimeOnly(16, 0), duration, London);

        Assert.False(result.IsSuccess);
        Assert.Equal("WeeklyPattern.DurationOutOfRange", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Enumerate_ReturnsMatchingDaysWithDstCorrectUtc()
    {
        // Mon/Thu 16:00 London, week of 2026-07-06 (BST +01:00 → 16:00 local == 15:00 UTC).
        var occ = Pattern().Enumerate(new DateOnly(2026, 7, 6), new DateOnly(2026, 7, 12));

        Assert.Collection(occ,
            o =>
            {
                Assert.Equal(new DateOnly(2026, 7, 6), o.OccurrenceDate); // Monday
                Assert.Equal(new DateTimeOffset(2026, 7, 6, 15, 0, 0, TimeSpan.Zero), o.StartUtc);
                Assert.Equal(new DateTimeOffset(2026, 7, 6, 16, 0, 0, TimeSpan.Zero), o.EndUtc);
            },
            o => Assert.Equal(new DateOnly(2026, 7, 9), o.OccurrenceDate)); // Thursday
    }
}
