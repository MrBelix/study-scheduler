using StudyScheduler.Domain.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Domain.Lessons;

public class WeekdaysTests
{
    [Theory]
    [InlineData(DayOfWeek.Sunday, Weekdays.Sunday)]
    [InlineData(DayOfWeek.Monday, Weekdays.Monday)]
    [InlineData(DayOfWeek.Saturday, Weekdays.Saturday)]
    public void ToWeekdays_MirrorsDayOfWeek(DayOfWeek day, Weekdays expected)
    {
        Assert.Equal(expected, day.ToWeekdays());
    }

    [Fact]
    public void Contains_ChecksTheMatchingBit()
    {
        var weekdays = Weekdays.Monday | Weekdays.Thursday;

        Assert.True(weekdays.Contains(DayOfWeek.Monday));
        Assert.True(weekdays.Contains(DayOfWeek.Thursday));
        Assert.False(weekdays.Contains(DayOfWeek.Tuesday));
    }

    [Theory]
    [InlineData(Weekdays.Monday, true)]
    [InlineData(Weekdays.All, true)]
    [InlineData(Weekdays.None, false)]
    [InlineData((Weekdays)(1 << 7), false)]
    public void IsValidSet_RequiresAtLeastOneKnownDayAndNoStrayBits(Weekdays weekdays, bool expected)
    {
        Assert.Equal(expected, weekdays.IsValidSet());
    }
}
