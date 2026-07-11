using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.Domain.Lessons;

/// <summary>
/// A weekly recurrence pattern: on <see cref="Days"/> at <see cref="StartTimeLocal"/> for
/// <see cref="DurationMinutes"/>, defined in <see cref="TimeZone"/>'s wall clock. A value object
/// (no identity — equal by value) that owns occurrence enumeration, so the recurrence logic lives
/// with the fields it depends on. A schedule change produces a new pattern, never mutates one.
/// </summary>
public sealed record WeeklyPattern
{
    private WeeklyPattern(Weekdays days, TimeOnly startTimeLocal, int durationMinutes, TimeZoneInfo timeZone)
    {
        Days = days;
        StartTimeLocal = startTimeLocal;
        DurationMinutes = durationMinutes;
        TimeZone = timeZone;
    }

    public Weekdays Days { get; }

    /// <summary>Wall-clock start time in <see cref="TimeZone"/>.</summary>
    public TimeOnly StartTimeLocal { get; }

    public int DurationMinutes { get; }

    public TimeZoneInfo TimeZone { get; }

    public static Result<WeeklyPattern> Create(
        Weekdays days, TimeOnly startTimeLocal, int durationMinutes, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var errors = new List<Error>();
        if (!days.IsValidSet())
            errors.Add(new Error(
                "WeeklyPattern.InvalidWeekdays", "At least one valid weekday is required.", "Weekdays"));
        if (durationMinutes is < Lesson.MinDurationMinutes or > Lesson.MaxDurationMinutes)
            errors.Add(new Error(
                "WeeklyPattern.DurationOutOfRange",
                $"Duration must be between {Lesson.MinDurationMinutes} and {Lesson.MaxDurationMinutes} minutes.",
                "DurationMinutes"));
        if (errors.Count > 0)
            return Result<WeeklyPattern>.Failure([.. errors]);

        return Result<WeeklyPattern>.Success(new WeeklyPattern(days, startTimeLocal, durationMinutes, timeZone));
    }

    /// <summary>
    /// Occurrences whose local date falls in <c>[fromLocal, toLocal]</c> (inclusive). Each local
    /// wall-clock start is converted to UTC via <see cref="WallClock"/>, so a 16:00 lesson stays at
    /// 16:00 local across DST transitions.
    /// </summary>
    public IReadOnlyList<LessonOccurrence> Enumerate(DateOnly fromLocal, DateOnly toLocal)
    {
        var occurrences = new List<LessonOccurrence>();
        for (var date = fromLocal; date <= toLocal; date = date.AddDays(1))
        {
            if (!Days.Contains(date.DayOfWeek))
                continue;

            var startUtc = WallClock.ToUtc(date, StartTimeLocal, TimeZone);
            occurrences.Add(new LessonOccurrence(date, startUtc, startUtc.AddMinutes(DurationMinutes)));
        }

        return occurrences;
    }
}
