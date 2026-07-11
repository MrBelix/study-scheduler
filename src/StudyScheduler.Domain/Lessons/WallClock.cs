namespace StudyScheduler.Domain.Lessons;

/// <summary>
/// The single DST-correct wall-clock -> UTC conversion used by recurring series
/// (<see cref="LessonSeries.GetOccurrences"/>). A local time that falls into the spring-forward
/// gap (an invalid local time) is shifted one hour later; for the ambiguous fall-back hour
/// <see cref="TimeZoneInfo.GetUtcOffset(System.DateTime)"/> picks the standard-time offset.
/// Keeping this in one place guarantees every occurrence resolves identically.
/// </summary>
public static class WallClock
{
    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
            local = local.AddHours(1);

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
