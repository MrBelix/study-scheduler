namespace StudyScheduler.Domain.Lessons;

/// <summary>
/// Days of week a <see cref="LessonSeries"/> runs on. The BCL's <see cref="DayOfWeek"/> is not a
/// flags enum, so this mirrors it as one (bit = <c>1 &lt;&lt; (int)DayOfWeek</c>) to allow
/// combinations like <c>Monday | Thursday</c>.
/// </summary>
[Flags]
public enum Weekdays
{
    None = 0,
    Sunday = 1 << (int)DayOfWeek.Sunday,
    Monday = 1 << (int)DayOfWeek.Monday,
    Tuesday = 1 << (int)DayOfWeek.Tuesday,
    Wednesday = 1 << (int)DayOfWeek.Wednesday,
    Thursday = 1 << (int)DayOfWeek.Thursday,
    Friday = 1 << (int)DayOfWeek.Friday,
    Saturday = 1 << (int)DayOfWeek.Saturday,
    All = Sunday | Monday | Tuesday | Wednesday | Thursday | Friday | Saturday,
}
