using StudyScheduler.Domain.Lessons;
using Xunit;

namespace StudyScheduler.Tests.Domain.Lessons;

public class WallClockTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    [Fact]
    public void ToUtc_WinterTime_UsesZeroOffset()
    {
        // 2026-01-15 is GMT (offset +00:00) in London.
        var utc = WallClock.ToUtc(new DateOnly(2026, 1, 15), new TimeOnly(12, 0), London);

        Assert.Equal(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero), utc);
    }

    [Fact]
    public void ToUtc_SummerTime_UsesDstOffset()
    {
        // 2026-07-15 is BST (offset +01:00) in London → 12:00 local == 11:00 UTC.
        var utc = WallClock.ToUtc(new DateOnly(2026, 7, 15), new TimeOnly(12, 0), London);

        Assert.Equal(new DateTimeOffset(2026, 7, 15, 11, 0, 0, TimeSpan.Zero), utc);
    }

    [Fact]
    public void ToUtc_SpringForwardGap_ShiftsInvalidTimeForward()
    {
        // Clocks jump 01:00 → 02:00 on 2026-03-29; 01:30 is an invalid local time.
        var invalid = new DateOnly(2026, 3, 29);
        Assert.True(London.IsInvalidTime(invalid.ToDateTime(new TimeOnly(1, 30))));

        // Shifted to 02:30 BST (+01:00) == 01:30 UTC — no exception, a real instant.
        var utc = WallClock.ToUtc(invalid, new TimeOnly(1, 30), London);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), utc);
    }
}
