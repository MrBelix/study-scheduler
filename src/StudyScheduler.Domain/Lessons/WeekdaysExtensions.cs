namespace StudyScheduler.Domain.Lessons;

/// <summary>
/// The bridge between the BCL's <see cref="DayOfWeek"/> and the <see cref="Weekdays"/> mask, plus the
/// mask's own validity rule. Kept next to the enum it operates on rather than inside
/// <see cref="WeeklyPattern"/>, so every holder of a mask reads it the same way.
/// </summary>
public static class WeekdaysExtensions
{
    public static Weekdays ToWeekdays(this DayOfWeek day) => (Weekdays)(1 << (int)day);

    public static bool Contains(this Weekdays weekdays, DayOfWeek day) =>
        (weekdays & day.ToWeekdays()) != 0;

    /// <summary>At least one day and no bits outside <see cref="Weekdays.All"/>.</summary>
    public static bool IsValidSet(this Weekdays weekdays) =>
        weekdays != Weekdays.None && (weekdays & ~Weekdays.All) == 0;
}
