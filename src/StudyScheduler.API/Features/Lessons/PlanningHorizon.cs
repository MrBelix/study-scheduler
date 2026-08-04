using StudyScheduler.Domain.Primitives;

namespace StudyScheduler.API.Features.Lessons;

/// <summary>
/// How far ahead the calendar is planned. One number in one place, used by everything that has an
/// opinion about the future:
/// <list type="bullet">
/// <item>the generator fills <c>[today, today + <see cref="Months"/> months]</c> with physical rows;</item>
/// <item>the nightly extender rolls that window forward, so it is genuinely rolling;</item>
/// <item>create and reschedule refuse a date past its edge — nothing may be planned into a stretch of
/// calendar the tutor cannot see filled in.</item>
/// </list>
/// A series is still allowed to END beyond the horizon: its rows simply stop at the edge and the
/// window catches up on later nights. Only lessons a user places by hand are capped.
/// </summary>
public static class PlanningHorizon
{
    /// <summary>
    /// Width of the rolling window. Four months covers a school term plus the slack a tutor needs to
    /// plan the next one, while keeping the number of generated rows per series in the low hundreds.
    /// </summary>
    public const int Months = 4;

    /// <summary>
    /// The last local date the window covers when anchored on <paramref name="anchorLocalDate"/> —
    /// "today" for the rolling window, the start date for a series' first batch. Dates are always
    /// local to the zone the schedule is expressed in.
    /// </summary>
    public static DateOnly LastDateFrom(DateOnly anchorLocalDate) => anchorLocalDate.AddMonths(Months);

    /// <summary>The refusal a date past the horizon earns, pointing at the field that carried it.</summary>
    public static Error Exceeded(string field) => new(
        "Lesson.BeyondPlanningHorizon",
        $"Lessons can be scheduled at most {Months} months ahead.",
        field);
}
