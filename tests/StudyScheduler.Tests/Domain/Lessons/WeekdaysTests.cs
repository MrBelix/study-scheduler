using StudyScheduler.Domain.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Domain.Lessons;

public class WeekdaysTests
{
    [Fact]
    public void IsValidSet_None_False() => Assert.False(Weekdays.None.IsValidSet());

    [Fact]
    public void IsValidSet_SingleAndCombined_True()
    {
        Assert.True(Weekdays.Monday.IsValidSet());
        Assert.True((Weekdays.Monday | Weekdays.Thursday).IsValidSet());
        Assert.True(Weekdays.All.IsValidSet());
    }

    [Fact]
    public void IsValidSet_BitsOutsideAll_False() =>
        Assert.False(((Weekdays)(1 << 7)).IsValidSet());

    [Theory]
    [InlineData(DayOfWeek.Monday, true)]
    [InlineData(DayOfWeek.Thursday, true)]
    [InlineData(DayOfWeek.Tuesday, false)]
    public void Contains_MatchesFlag(DayOfWeek day, bool expected) =>
        Assert.Equal(expected, (Weekdays.Monday | Weekdays.Thursday).Contains(day));
}
